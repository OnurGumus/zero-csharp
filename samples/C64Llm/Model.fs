// A Llama-style language model in integer arithmetic only: no floating
// point, which a 6502 doesn't have (llvm-mos's software floats would cost
// 20 KB of the C64's 50).
//
// The model is Karpathy's stories260K (models/): 5 layers, 64 channels,
// 8 attention heads sharing 4 key/value heads, a 172-channel SwiGLU
// feed-forward block and a 512-token vocabulary. The host tool (tool/)
// turns it into an REU image (Image below); the C64 copies a row of weights
// at a time into its RAM, as it needs it.
//
// Numbers:
//   - activations are Q16.16 (an int holding value * 65536);
//   - weights are int8, each row with its own scale, a power of two 2^-k;
//   - a row times a vector: the vector is rounded to int8 with a shared
//     power-of-two scale, the row's dot product is an int, then the row's
//     and vector's scales bring it back to Q16.16;
//   - RMS norm uses an integer square root; softmax and SiLU use tables of
//     exp(-x) and sigmoid(x) with linear interpolation; RoPE a table of
//     cosines and sines.
// This file is the model: its shape, the order of the steps, the key/value
// cache. The arithmetic over vectors and the tokenizer are Machine.fs's,
// the same F# on the C64 and on the host, so the host can check the C64.
module Llm64.Model

open FSharp.NativeInterop
open Llm64.Machine

#nowarn "9" // native pointers (Machine.fs says why)

/// The REU image: a header of int32 fields at REU address 0.
module Image =
    /// "LL66". (Older images, which this program would misread: "LL65",
    /// with a scale m / 2^k per row, and "LL64", with each weight stored
    /// plus 128.)
    [<Literal>]
    let Magic = 0x36364C4C

    // Field numbers in the header.
    [<Literal>]
    let Dim = 1
    [<Literal>]
    let Hidden = 2
    [<Literal>]
    let Layers = 3
    [<Literal>]
    let Heads = 4
    [<Literal>]
    let KvHeads = 5
    [<Literal>]
    let Vocab = 6
    [<Literal>]
    let SeqLen = 7
    [<Literal>]
    let Embedding = 8
    [<Literal>]
    let RmsAttention = 9
    [<Literal>]
    let Wq = 10
    [<Literal>]
    let Wk = 11
    [<Literal>]
    let Wv = 12
    [<Literal>]
    let Wo = 13
    [<Literal>]
    let RmsFfn = 14
    [<Literal>]
    let W1 = 15
    [<Literal>]
    let W2 = 16
    [<Literal>]
    let W3 = 17
    [<Literal>]
    let RmsFinal = 18
    [<Literal>]
    let Rope = 19
    [<Literal>]
    let ExpTable = 20
    [<Literal>]
    let SigmoidTable = 21
    [<Literal>]
    let Tokenizer = 22
    [<Literal>]
    let TokenizerLength = 23
    [<Literal>]
    let KvCache = 24
    [<Literal>]
    let End = 25
    [<Literal>]
    let Fields = 26

    /// A weight row: k (the row's scale is 2^-k), then the int8 weights.
    [<Literal>]
    let RowHeader = Machine.RowHeader

    /// exp(-i/16) for i in 0..256, as Q1.15.
    [<Literal>]
    let ExpEntries = 257
    /// sigmoid((i - 128) / 16) for i in 0..256, as Q1.15.
    [<Literal>]
    let SigmoidEntries = 257

    /// The tokenizer's lookup table: piece bytes -> id + 1 (0: empty).
    [<Literal>]
    let HashSize = 1024

/// stories260K's shape. The program is built for it (constant loop bounds
/// are much less 6502 code than numbers read at run time); Llm.load checks
/// that the image has the same shape.
module Shape =
    [<Literal>]
    let Dim = 64
    [<Literal>]
    let Hidden = 172
    [<Literal>]
    let Layers = 5
    [<Literal>]
    let Heads = 8
    [<Literal>]
    let KvHeads = 4
    [<Literal>]
    let Vocab = 512
    /// The context: the prompt and the story, in tokens.
    [<Literal>]
    let SeqLen = 128
    [<Literal>]
    let HeadSize = Dim / Heads
    [<Literal>]
    let KvDim = Dim * KvHeads / Heads
    [<Literal>]
    let KvMul = Heads / KvHeads
    /// A position's keys and values in the cache: KvDim int16 each.
    [<Literal>]
    let KvRecord = KvDim * 4

open Shape

/// A layer's weight matrices: attention's query, key, value and output, and
/// the feed-forward block's W1 and W3 in, W2 out. Llm.locate is inline, so
/// each `project Wq ...` folds to the constants of its case: the union is
/// no code at all on the 6502 (the program came out 96 bytes smaller).
[<Struct>]
type Matrix =
    | Wq
    | Wk
    | Wv
    | Wo
    | W1
    | W2
    | W3

/// Where the model's buffers are in the RAM under the KERNAL ROM, in bytes
/// from its start, after the kernels' (Machine.Map).
module Layout =
    [<Literal>]
    let Header = Map.End
    [<Literal>]
    let X = Header + Image.Fields * 4
    [<Literal>]
    let Xb = X + Dim * 4
    [<Literal>]
    let Q = Xb + Dim * 4
    [<Literal>]
    let K = Q + Dim * 4
    [<Literal>]
    let V = K + KvDim * 4
    /// The feed-forward block's hidden values (Hidden int32), and at
    /// another time attention's scores (KvMul * SeqLen int16, fewer bytes).
    [<Literal>]
    let Scratch = V + KvDim * 4
    [<Literal>]
    let Xq = Scratch + Hidden * 4
    [<Literal>]
    let Rope = Xq + Hidden
    [<Literal>]
    let Shape = Rope + HeadSize * 2
    [<Literal>]
    let End = Shape + 5 * 4

/// The model and its run state.
module Llm =
    let header: nativeptr<byte> = high Layout.Header

    /// Header field i: a number.
    [<NoCompilerInlining>]
    let field i = getInt header (i * 4)

    /// Header field i: where something is in the REU.
    let inline address i = field i * 1<reu>

    let private x: nativeptr<int> = high Layout.X
    let private xb: nativeptr<int> = high Layout.Xb
    let private q: nativeptr<int> = high Layout.Q
    let private k: nativeptr<int> = high Layout.K
    let private v: nativeptr<int> = high Layout.V
    let private hb: nativeptr<int> = high Layout.Scratch
    let private scores: nativeptr<int16> = high Layout.Scratch
    let private xq: nativeptr<sbyte> = high Layout.Xq
    let private rope: nativeptr<byte> = high Layout.Rope
    let private shape: nativeptr<int> = high Layout.Shape

    /// Reads the header and the tables from the REU. False unless it holds
    /// an image of this shape (with no REU it reads zeros).
    let load () =
        init ()
        fetch 0<reu> header (Image.Fields * 4)
        let ok =
            field 0 = Image.Magic && field Image.Dim = Dim && field Image.Hidden = Hidden
            && field Image.Layers = Layers && field Image.Heads = Heads && field Image.KvHeads = KvHeads
            && field Image.Vocab = Vocab && field Image.SeqLen = SeqLen
        if ok then
            set shape 0 Heads
            set shape 1 KvMul
            set shape 2 HeadSize
            set shape 3 KvDim
            set shape 4 SeqLen
        ok

    /// Where a layer's matrix starts in the REU, with its rows and columns.
    let inline private locate matrix (layer: int) =
        let struct (at, rows, cols) =
            match matrix with
            | Wq -> struct (Image.Wq, Dim, Dim)
            | Wk -> struct (Image.Wk, KvDim, Dim)
            | Wv -> struct (Image.Wv, KvDim, Dim)
            | Wo -> struct (Image.Wo, Dim, Dim)
            | W1 -> struct (Image.W1, Hidden, Dim)
            | W2 -> struct (Image.W2, Dim, Hidden)
            | W3 -> struct (Image.W3, Hidden, Dim)
        struct (address at +. layer * rows * (cols + Image.RowHeader), rows, cols)

    /// out = matrix input, with input already rounded into xq with scale e.
    let inline private project matrix layer e (out: nativeptr<int>) =
        let struct (at, rows, cols) = locate matrix layer
        matVecRows at rows cols xq e out

    /// out = matrix input.
    let inline private multiply matrix layer (input: nativeptr<int>) out =
        let struct (_, _, cols) = locate matrix layer
        project matrix layer (quantize input cols xq) out

    /// The REU address of a layer's matrix.
    let inline private start matrix layer =
        let struct (at, _, _) = locate matrix layer
        at

    // One layer at position: attention, then the feed-forward block, each
    // added to x.
    let private layer (l: int) (position: int) =
        // RMS norm, with the gains in the REU: the mean square is Q32.32
        // plus 1e-5, and its integer square root Q16.16.
        rmsNorm xb x (address Image.RmsAttention +. l * Dim * 4) Dim
        // q, k and v from the same rounded xb.
        let e = quantize xb Dim xq
        project Wq l e q
        project Wk l e k
        project Wv l e v
        Machine.rope q k Dim KvDim HeadSize rope

        // This position's keys and values join the cache, as Q8.8.
        let layerCache = address Image.KvCache +. l * SeqLen * KvRecord
        cache k v KvDim (layerCache +. position * KvRecord)
        attention shape q layerCache position scores (address Image.ExpTable) xb
        // (q is free again: the output goes there.)
        multiply Wo l xb q
        add x q Dim

        // Feed-forward: w2 (silu(w1 x) * w3 x).
        rmsNorm xb x (address Image.RmsFfn +. l * Dim * 4) Dim
        gate (start W1 l) (start W3 l) Hidden Dim xq (quantize xb Dim xq) (address Image.SigmoidTable) hb
        multiply W2 l hb xb
        add x xb Dim

    /// Runs the model on token at position (0, 1, 2, ... in order, below
    /// SeqLen) and returns the likeliest next token.
    let next (token: int) (position: int) =
        embed (address Image.Embedding +. token * (Dim + Image.RowHeader)) x Dim
        // This position's rotations: cos, sin as Q1.14 per pair of channels.
        fetch (address Image.Rope +. position * HeadSize * 2) rope (HeadSize * 2)
        for l in 0 .. Layers - 1 do
            layer l position
        rmsNorm x x (address Image.RmsFinal) Dim
        // The classifier is the embedding table; only its largest result
        // is kept.
        classify (address Image.Embedding) Vocab Dim xq (quantize x Dim xq)

/// The tokenizer: llama2.c's, with its scores turned into ranks. Its block
/// in the image (Image.Tokenizer):
///   offsets  uint16 per token, into the pieces
///   lengths  byte per token
///   ranks    int16 per token (higher merges first)
///   table    Image.HashSize uint16: id + 1 by hash of the piece
///   pieces   the bytes
/// All of it stays in the REU and is read as it is needed. The work is
/// Machine.fs's (kernels.c on the C64).
module Tokenizer =
    let mutable private start = 0<reu>

    /// Finds the tokenizer in the image (after Llm.load).
    let load () = start <- Llm.address Image.Tokenizer

    /// Text (length bytes) to ids, as llama2.c's encode: BOS, a space piece,
    /// a piece per byte (or the byte + 3), then repeatedly the adjacent pair
    /// whose joined piece ranks highest is merged. ids needs room for
    /// length + 2. Returns the count. (llama2.c takes a UTF-8 character at
    /// a time; the same for ASCII, which is all a C64 types.)
    let encode (text: nativeptr<byte>) (length: int) (ids: nativeptr<int>) = Machine.encode start text length ids

    /// The text of token (after previous) into out, which needs room for
    /// 32 bytes; returns its length. No leading space right after BOS, and
    /// the byte itself for a piece like "<0x0A>".
    let decode (previous: int) (token: int) (out: nativeptr<byte>) = Machine.decode start previous token out
