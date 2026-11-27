// The same model in float32, as llama2.c's run.c computes it: the reference
// the integer model is measured against.
module Llm64.Float

open System
open Llm64.Pack

type FloatLlm(m: FloatModel) =
    let dim, hidden, heads = m.Dim, m.Hidden, m.Heads
    let headSize, kvDim = m.HeadSize, m.KvDim
    let kvMul = heads / m.KvHeads
    let x = Array.zeroCreate<float32> dim
    let xb = Array.zeroCreate<float32> dim
    let xb2 = Array.zeroCreate<float32> dim
    let q = Array.zeroCreate<float32> dim
    let hb = Array.zeroCreate<float32> hidden
    let hb2 = Array.zeroCreate<float32> hidden
    let att = Array.zeroCreate<float32> m.SeqLen
    let logits = Array.zeroCreate<float32> m.Vocab
    let keys = Array.zeroCreate<float32> (m.Layers * m.SeqLen * kvDim)
    let values = Array.zeroCreate<float32> (m.Layers * m.SeqLen * kvDim)

    let rmsNorm (out: float32[]) (input: float32[]) (g: float32[]) (at: int) =
        let mutable ss = 0.0f
        for v in input do ss <- ss + v * v
        let s = 1.0f / sqrt (ss / float32 dim + 1e-5f)
        for i in 0 .. dim - 1 do out.[i] <- g.[at + i] * (s * input.[i])

    let matVec (out: float32[]) (outAt: int) (w: float32[]) (at: int) (input: float32[]) (n: int) (d: int) =
        for i in 0 .. d - 1 do
            let mutable sum = 0.0f
            for j in 0 .. n - 1 do sum <- sum + w.[at + i * n + j] * input.[j]
            out.[outAt + i] <- sum

    member _.Forward(token: int, pos: int) =
        Array.blit m.Embedding (token * dim) x 0 dim
        for l in 0 .. m.Layers - 1 do
            rmsNorm xb x m.RmsAttention (l * dim)
            let cacheAt = (l * m.SeqLen + pos) * kvDim
            matVec q 0 m.Wq (l * dim * dim) xb dim dim
            matVec keys cacheAt m.Wk (l * dim * kvDim) xb dim kvDim
            matVec values cacheAt m.Wv (l * dim * kvDim) xb dim kvDim
            let mutable i = 0
            while i < dim do
                let f = 1.0f / MathF.Pow(10000.0f, float32 (i % headSize) / float32 headSize)
                let v = float32 pos * f
                let c, s = MathF.Cos v, MathF.Sin v
                let q0, q1 = q.[i], q.[i + 1]
                q.[i] <- q0 * c - q1 * s
                q.[i + 1] <- q0 * s + q1 * c
                if i < kvDim then
                    let k0, k1 = keys.[cacheAt + i], keys.[cacheAt + i + 1]
                    keys.[cacheAt + i] <- k0 * c - k1 * s
                    keys.[cacheAt + i + 1] <- k0 * s + k1 * c
                i <- i + 2
            for h in 0 .. heads - 1 do
                let kvOff = (h / kvMul) * headSize
                for t in 0 .. pos do
                    let mutable score = 0.0f
                    for j in 0 .. headSize - 1 do
                        score <- score + q.[h * headSize + j] * keys.[(l * m.SeqLen + t) * kvDim + kvOff + j]
                    att.[t] <- score / sqrt (float32 headSize)
                let mutable largest = att.[0]
                for t in 1 .. pos do largest <- max largest att.[t]
                let mutable sum = 0.0f
                for t in 0 .. pos do
                    att.[t] <- exp (att.[t] - largest)
                    sum <- sum + att.[t]
                for j in 0 .. headSize - 1 do
                    let mutable acc = 0.0f
                    for t in 0 .. pos do
                        acc <- acc + att.[t] / sum * values.[(l * m.SeqLen + t) * kvDim + kvOff + j]
                    xb.[h * headSize + j] <- acc
            matVec xb2 0 m.Wo (l * dim * dim) xb dim dim
            for j in 0 .. dim - 1 do x.[j] <- x.[j] + xb2.[j]
            rmsNorm xb x m.RmsFfn (l * dim)
            matVec hb 0 m.W1 (l * dim * hidden) xb dim hidden
            matVec hb2 0 m.W3 (l * dim * hidden) xb dim hidden
            for j in 0 .. hidden - 1 do
                let a = hb.[j]
                hb.[j] <- a * (1.0f / (1.0f + exp -a)) * hb2.[j]
            matVec xb 0 m.W2 (l * dim * hidden) hb hidden dim
            for j in 0 .. dim - 1 do x.[j] <- x.[j] + xb.[j]
        rmsNorm x x m.RmsFinal 0
        matVec logits 0 m.Embedding 0 x dim m.Vocab
        logits
