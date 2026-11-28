// The C64 language model's host tool.
//
//   pack    <model.bin> <tokenizer.bin> <out.reu>
//           the REU image
//   run     <image.reu> <prompt> [steps]
//           runs the integer model as the C64 will (Model.fs, with
//           Machine.fs's F# kernels), printing its story and token ids
//   compare <model.bin> <tokenizer.bin> <image.reu> [steps]
//           measures the integer model against the float one
module Llm64.Tool

#nowarn "9" // native pointers

open System
open System.IO
open System.Text
open Llm64.Machine
open Llm64.Model
open Llm64.Pack
open Llm64.Float

let prompts =
    [ "Once upon a time"
      "Lily and Ben went to the park"
      "The cat sat on the mat"
      "Tom had a red ball"
      "One day, a little bird"
      "Sara was sad because" ]

let loadImage (path: string) =
    let bytes = File.ReadAllBytes path
    let size = BitConverter.ToInt32(bytes, Image.End * 4)
    Machine.image <- Array.zeroCreate (max size bytes.Length)
    bytes.CopyTo(Machine.image, 0)
    if not (Llm.load ()) then failwith "not an image of stories260K packed by this version of the tool"
    Tokenizer.load ()

/// The prompt's ids: BOS first.
let encode (prompt: string) =
    let bytes = Encoding.UTF8.GetBytes prompt
    let text = alloc<byte> (bytes.Length + 1)
    bytes |> Array.iteri (fun i b -> set text i b)
    let ids = alloc<int> (bytes.Length + 2)
    let count = Tokenizer.encode text bytes.Length ids
    Array.init count (fun i -> get ids i)

/// Greedy generation with the integer model: ids after BOS, prompt included.
let generate (prompt: string) (steps: int) =
    let ids = encode prompt
    let out = ResizeArray<int>()
    let mutable token = 1
    let mutable pos = 0
    let mutable stop = false
    while not stop && pos + 1 < steps do
        let likeliest = Llm.next token pos
        let next = if pos + 1 < ids.Length then ids.[pos + 1] else likeliest
        if next = 1 then stop <- true
        else
            out.Add next
            token <- next
            pos <- pos + 1
    out

let decode (ids: seq<int>) =
    let bytes = ResizeArray<byte>()
    let piece = alloc<byte> 32
    let mutable previous = 1
    for id in ids do
        let n = Tokenizer.decode previous id piece
        for i in 0 .. n - 1 do
            bytes.Add(get piece i)
        previous <- id
    Encoding.UTF8.GetString(bytes.ToArray())

let floatArgmax (logits: float32[]) =
    let mutable best = 0
    for i in 1 .. logits.Length - 1 do
        if logits.[i] > logits.[best] then best <- i
    best

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | "pack" :: model :: tokenizer :: out :: _ ->
        let m = readModel model
        let t = readTokenizer tokenizer m.Vocab
        let bytes = image m t Shape.SeqLen
        File.WriteAllBytes(out, bytes)
        let field i = BitConverter.ToInt32(bytes, i * 4)
        printfn "%s: for a %d KB REU (the model %d KB, then the key/value cache at $%06X)"
            out (bytes.Length / 1024) ((field Image.TokenizerLength + field Image.Tokenizer + 1023) / 1024) (field Image.KvCache)
        0
    | "run" :: path :: prompt :: rest ->
        let steps = match rest with s :: _ -> int s | [] -> 64
        loadImage path
        let ids = generate prompt steps
        printfn "%s" (decode ids)
        printfn "%s" (String.Join(" ", ids))
        0
    | "compare" :: model :: _ :: path :: rest ->
        let steps = match rest with s :: _ -> int s | [] -> 64
        let m = readModel model
        loadImage path
        let mutable agree = 0
        let mutable total = 0
        for prompt in prompts do
            // Both models read the float model's greedy story; how often
            // does the integer one pick the same next token?
            let fl = FloatLlm(m)
            let ids = encode prompt
            let story = ResizeArray<int>()
            let mutable token = 1
            let mutable firstDiff = -1
            let mutable pos = 0
            while pos + 1 < steps do
                let f = floatArgmax (fl.Forward(token, pos))
                let i = Llm.next token pos
                let next = if pos + 1 < ids.Length then ids.[pos + 1] else f
                if pos + 1 >= ids.Length then
                    total <- total + 1
                    if f = i then agree <- agree + 1
                    elif firstDiff < 0 then firstDiff <- pos + 1
                story.Add next
                token <- next
                pos <- pos + 1
            let intStory = generate prompt steps
            printfn "%-32s first different pick at %s" prompt (if firstDiff < 0 then "none" else string firstDiff)
            printfn "  float:   %s" (decode (Seq.takeWhile (fun t -> t <> 1) story))
            printfn "  integer: %s" (decode intStory)
        printfn "same next token: %d of %d (%.1f%%)" agree total (100.0 * float agree / float total)
        0
    | _ ->
        eprintfn "usage: pack <model> <tokenizer> <out.reu> | run <image> <prompt> [steps] | compare <model> <tokenizer> <image> [steps]"
        2
