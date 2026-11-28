// Turns llama2.c's model and tokenizer files into the REU image that
// Model.fs reads (layout: Model.Image).
module Llm64.Pack

open System
open System.IO
open Llm64.Model

/// llama2.c's float model, as read from its file.
type FloatModel =
    { Dim: int
      Hidden: int
      Layers: int
      Heads: int
      KvHeads: int
      Vocab: int
      SeqLen: int
      Embedding: float32[]
      RmsAttention: float32[]
      Wq: float32[]
      Wk: float32[]
      Wv: float32[]
      Wo: float32[]
      RmsFfn: float32[]
      W1: float32[]
      W2: float32[]
      W3: float32[]
      RmsFinal: float32[] }

    member m.HeadSize = m.Dim / m.Heads
    member m.KvDim = m.Dim * m.KvHeads / m.Heads

let readModel (path: string) =
    let bytes = File.ReadAllBytes path
    let header i = BitConverter.ToInt32(bytes, i * 4)
    let dim, hidden, layers, heads, kvHeads = header 0, header 1, header 2, header 3, header 4
    let vocab = abs (header 5)
    if header 5 < 0 then failwith "a classifier of its own isn't supported (stories260K shares the embedding)"
    let seqLen = header 6
    let kvDim = dim * kvHeads / heads
    let mutable at = 28
    let take n =
        let a = Array.init n (fun i -> BitConverter.ToSingle(bytes, at + i * 4))
        at <- at + n * 4
        a
    let embedding = take (vocab * dim)
    let rmsAttention = take (layers * dim)
    let wq = take (layers * dim * dim)
    let wk = take (layers * dim * kvDim)
    let wv = take (layers * dim * kvDim)
    let wo = take (layers * dim * dim)
    let rmsFfn = take (layers * dim)
    let w1 = take (layers * hidden * dim)
    let w2 = take (layers * dim * hidden)
    let w3 = take (layers * hidden * dim)
    let rmsFinal = take dim
    { Dim = dim; Hidden = hidden; Layers = layers; Heads = heads; KvHeads = kvHeads; Vocab = vocab; SeqLen = seqLen
      Embedding = embedding; RmsAttention = rmsAttention; Wq = wq; Wk = wk; Wv = wv; Wo = wo; RmsFfn = rmsFfn
      W1 = w1; W2 = w2; W3 = w3; RmsFinal = rmsFinal }

/// llama2.c's tokenizer: pieces and scores.
type FloatTokenizer = { Pieces: byte[][]; Scores: float32[] }

let readTokenizer (path: string) (vocab: int) =
    let bytes = File.ReadAllBytes path
    let mutable at = 4
    let pieces = Array.zeroCreate vocab
    let scores = Array.zeroCreate vocab
    for id in 0 .. vocab - 1 do
        scores.[id] <- BitConverter.ToSingle(bytes, at)
        let n = BitConverter.ToInt32(bytes, at + 4)
        pieces.[id] <- bytes.[at + 8 .. at + 8 + n - 1]
        at <- at + 8 + n
    { Pieces = pieces; Scores = scores }

/// A row of weights as int8 with a scale of 2^-k: the k, then the weights,
/// each round(w * 2^k), the largest k for which they all fit -127..127. A
/// power of two, so that the C64 scales a row's sum with a shift instead of
/// a multiplication (Machine.fs: rowTimes); the largest weight comes out
/// between 64 and 127.
let quantizeRow (row: ReadOnlySpan<float32>) (out: ResizeArray<byte>) =
    let mutable largest = 0.0
    for w in row do
        largest <- max largest (abs (float w))
    let mutable k = 0
    while k < 60 && Math.Round(largest * Math.Pow(2.0, float (k + 1))) <= 127.0 do
        k <- k + 1
    if Math.Round(largest * Math.Pow(2.0, float k)) > 127.0 then failwithf "row scale %g out of range" largest
    out.Add(byte k)
    for w in row do
        // -127..127, as Kernels.DotI8 needs.
        out.Add(byte (sbyte (Math.Clamp(int (Math.Round(float w * Math.Pow(2.0, float k))), -127, 127))))

let private addMatrix (out: ResizeArray<byte>) (weights: float32[]) (rows: int) (cols: int) =
    for r in 0 .. weights.Length / cols - 1 do
        quantizeRow (ReadOnlySpan(weights, r * cols, cols)) out
    ignore rows

let private addInt (out: ResizeArray<byte>) (v: int) =
    out.AddRange(BitConverter.GetBytes v)

let private addI16 (out: ResizeArray<byte>) (v: int) =
    out.AddRange(BitConverter.GetBytes(int16 v))

let private q16 (v: float) = int (Math.Round(v * 65536.0))

/// Model.Tokenizer.hash, of a piece's bytes.
let hash (piece: byte[]) =
    let mutable h = 0
    for b in piece do
        h <- (h * 33 + int b) &&& 0xFFFF
    h &&& (Image.HashSize - 1)

/// The REU image of model and tokenizer, with room for seqLen positions.
let image (model: FloatModel) (tokenizer: FloatTokenizer) (seqLen: int) =
    let out = ResizeArray<byte>()
    let fields = Array.zeroCreate Image.Fields
    let mark i = fields.[i] <- out.Count
    out.AddRange(Array.zeroCreate (Image.Fields * 4))
    let headSize = model.HeadSize

    mark Image.Embedding
    addMatrix out model.Embedding model.Vocab model.Dim
    mark Image.RmsAttention
    for g in model.RmsAttention do addInt out (q16 (float g))
    mark Image.Wq
    addMatrix out model.Wq model.Dim model.Dim
    mark Image.Wk
    addMatrix out model.Wk model.KvDim model.Dim
    mark Image.Wv
    addMatrix out model.Wv model.KvDim model.Dim
    mark Image.Wo
    addMatrix out model.Wo model.Dim model.Dim
    mark Image.RmsFfn
    for g in model.RmsFfn do addInt out (q16 (float g))
    mark Image.W1
    addMatrix out model.W1 model.Hidden model.Dim
    mark Image.W2
    addMatrix out model.W2 model.Dim model.Hidden
    mark Image.W3
    addMatrix out model.W3 model.Hidden model.Dim
    mark Image.RmsFinal
    for g in model.RmsFinal do addInt out (q16 (float g))

    // RoPE: per position, per pair of channels in a head, cos and sin as Q1.14.
    mark Image.Rope
    for position in 0 .. seqLen - 1 do
        for pair in 0 .. headSize / 2 - 1 do
            let frequency = 1.0 / Math.Pow(10000.0, float (pair * 2) / float headSize)
            let angle = float position * frequency
            addI16 out (int (Math.Round(Math.Cos angle * 16384.0)))
            addI16 out (int (Math.Round(Math.Sin angle * 16384.0)))

    mark Image.ExpTable
    for i in 0 .. Image.ExpEntries - 1 do
        addI16 out (int (Math.Round(32768.0 * Math.Exp(-float i / 16.0))))
    mark Image.SigmoidTable
    for i in 0 .. Image.SigmoidEntries - 1 do
        let x = float (i - 128) / 16.0
        addI16 out (int (Math.Round(32768.0 / (1.0 + Math.Exp -x))))

    // The tokenizer (layout: Model.Tokenizer).
    mark Image.Tokenizer
    let vocab = model.Vocab
    let blob = ResizeArray<byte>()
    let offsets = Array.zeroCreate vocab
    for id in 0 .. vocab - 1 do
        offsets.[id] <- blob.Count
        blob.AddRange tokenizer.Pieces.[id]
    if blob.Count > 0xFFFF then failwith "the tokenizer's pieces don't fit 16-bit offsets"
    // Ranks: dense, by score; equal scores share a rank.
    let distinct = tokenizer.Scores |> Array.distinct |> Array.sort
    if distinct.Length > 32767 then failwith "too many distinct scores"
    let rank (s: float32) = Array.BinarySearch(distinct, s)
    let table = Array.zeroCreate Image.HashSize
    for id in 0 .. vocab - 1 do
        let piece = tokenizer.Pieces.[id]
        let mutable slot = hash piece
        let mutable placed = false
        while not placed do
            let entry = table.[slot]
            if entry = 0 then
                table.[slot] <- id + 1
                placed <- true
            elif tokenizer.Pieces.[entry - 1] = piece then
                placed <- true // the first id keeps a duplicated piece
            else
                slot <- (slot + 1) &&& (Image.HashSize - 1)
    let start = out.Count
    for id in 0 .. vocab - 1 do addI16 out offsets.[id]
    for id in 0 .. vocab - 1 do out.Add(byte tokenizer.Pieces.[id].Length)
    for id in 0 .. vocab - 1 do addI16 out (rank tokenizer.Scores.[id])
    for e in table do addI16 out e
    out.AddRange blob
    fields.[Image.TokenizerLength] <- out.Count - start

    // The key/value cache follows, on a 64 KB boundary; it isn't in the file.
    let cache = (out.Count + 0xFFFF) &&& ~~~0xFFFF
    fields.[Image.KvCache] <- cache
    fields.[Image.End] <- cache + model.Layers * seqLen * model.KvDim * 4

    fields.[0] <- Image.Magic
    fields.[Image.Dim] <- model.Dim
    fields.[Image.Hidden] <- model.Hidden
    fields.[Image.Layers] <- model.Layers
    fields.[Image.Heads] <- model.Heads
    fields.[Image.KvHeads] <- model.KvHeads
    fields.[Image.Vocab] <- model.Vocab
    fields.[Image.SeqLen] <- seqLen
    // As big as the smallest REU it fits (128 KB, 256 KB, ... 16 MB): an
    // REU image file is the whole REU (VICE refuses one of another size).
    let mutable size = 128 * 1024
    while size < fields.[Image.End] do
        size <- size * 2
    let bytes = Array.zeroCreate size
    out.CopyTo(bytes)
    for i in 0 .. Image.Fields - 1 do
        BitConverter.GetBytes(fields.[i]).CopyTo(bytes, i * 4)
    bytes
