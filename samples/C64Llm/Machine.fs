// The model's inner loops, in integers only, the same F# on the C64 and on
// the host: the host tool (tool/) runs them over an image in memory and so
// says exactly what the C64 will print.
//
// What differs between the two is small, at the top: where the model is
// (on the C64 a RAM Expansion Unit, which lib's C64.Reu copies from and to;
// on the host a byte array), the int8 dot product (lib's Kernels.DotI8, in
// 6502 assembly on the C64), and the clock.
//
// The 6502 does a byte at a time what F# writes as 32-bit arithmetic, and
// has no multiply instruction, so the code keeps to what is cheap there:
//   - 64-bit products are made from 32-bit ones (mulShift);
//   - buffers are native memory (nativeptr), not arrays: an array costs a
//     null check, a bounds check and a save for the garbage collector at
//     every use, three times the code;
//   - the buffers are at fixed places in the 8 KB of RAM under the C64's
//     KERNAL ROM, which init switches out for the whole run: a constant
//     address each, which takes nothing from the 50 KB the program and its
//     heap share.
module Llm64.Machine

open System
open FSharp.NativeInterop
open System.Runtime.InteropServices

#nowarn "9" // native pointers

/// A byte address in the REU. The C64 can't read one, only copy from it
/// (fetch) or to it (store), so the unit keeps REU addresses and the C64's
/// own apart: mixing them up doesn't compile. Units of measure exist only
/// for the compiler; the 6502 sees plain numbers.
[<Measure>]
type reu

/// n bytes further on in the REU.
let inline (+.) (at: int<reu>) (n: int) : int<reu> = at + n * 1<reu>

/// n zeroed 'T, never freed.
let alloc<'T when 'T: unmanaged> (n: int) : nativeptr<'T> =
    NativePtr.ofVoidPtr (NativeMemory.AllocZeroed(unativeint (n * sizeof<'T>)))

let inline get (p: nativeptr<'T>) (i: int) = NativePtr.get p i

let inline set (p: nativeptr<'T>) (i: int) (value: 'T) = NativePtr.set p i value

/// p, read as other values from byte offset o on.
let inline cast<'T when 'T: unmanaged> (p: nativeptr<byte>) (o: int) : nativeptr<'T> =
    NativePtr.ofNativeInt (NativePtr.toNativeInt p + nativeint o)

/// The int32 at byte offset o of p (little-endian, as both machines are).
let inline getInt (p: nativeptr<byte>) (o: int) : int = NativePtr.read (cast<int> p o)

/// The uint16 at byte offset o of p.
let inline getU16 (p: nativeptr<byte>) (o: int) : int = int (NativePtr.read (cast<uint16> p o))

/// The int16 at byte offset o of p.
let inline getI16 (p: nativeptr<byte>) (o: int) : int = int (NativePtr.read (cast<int16> p o))

// ---- what differs -----------------------------------------------------------

#if C64
/// Copies count bytes from the REU at at to p.
let fetch (at: int<reu>) (p: nativeptr<byte>) (count: int) =
    C64.Reu.Read(int at, Span<byte>(NativePtr.toVoidPtr p, count))

/// Copies count bytes from p to the REU at at.
let store (p: nativeptr<byte>) (at: int<reu>) (count: int) =
    C64.Reu.Write(int at, ReadOnlySpan<byte>(NativePtr.toVoidPtr p, count))

/// The sum of the n products a[i] * b[i] (-127..127 each).
let dot (a: nativeptr<sbyte>) (b: nativeptr<sbyte>) (n: int) = Kernels.DotI8(a, b, n)

/// Tenths of seconds since the machine started (C64.Ticks counts sixtieths;
/// with the ROM out the KERNAL's interrupt goes on counting them).
let clock () = int (C64.Ticks / 6u)

/// Switches the KERNAL ROM out for the rest of the run, so that the RAM
/// under it, where the buffers are, reads as itself. Printing and reading
/// the keyboard go on working (C64.KernalRom says how).
let init () = C64.KernalRom <- false

/// The buffer at offset in the RAM under the KERNAL ROM ($E000 on).
let inline high<'T when 'T: unmanaged> (offset: int) : nativeptr<'T> =
    NativePtr.ofNativeInt (nativeint (0xE000 + offset))
#else
/// The host's stand-in for the REU.
let mutable image: byte[] = Array.zeroCreate 0

let fetch (at: int<reu>) (p: nativeptr<byte>) (count: int) =
    Marshal.Copy(image, int at, NativePtr.toNativeInt p, count)

let store (p: nativeptr<byte>) (at: int<reu>) (count: int) =
    Marshal.Copy(NativePtr.toNativeInt p, image, int at, count)

let dot (a: nativeptr<sbyte>) (b: nativeptr<sbyte>) (n: int) =
    let mutable sum = 0
    for i in 0 .. n - 1 do
        sum <- sum + int (get a i) * int (get b i)
    sum

/// No clock on the host.
let clock () = 0

/// Nothing to switch on the host.
let init () = ()

/// The host's stand-in for the RAM under the KERNAL ROM.
let highMemory: nativeptr<byte> = alloc 8192

let inline high<'T when 'T: unmanaged> (offset: int) : nativeptr<'T> = cast<'T> highMemory offset
#endif

/// Where the kernels' buffers are, in bytes from the start of the RAM under
/// the KERNAL ROM; Model.fs's vectors come after them. Constant offsets, so
/// that each buffer is a constant address: buffers made at the start would
/// be module values, which a static constructor sets up and every use
/// checks (1.6 KB of 6502 code).
module Map =
    /// A row of weights: k, then up to 256 int8.
    [<Literal>]
    let Row = 0
    /// One key/value head's keys or values at a position, int16.
    [<Literal>]
    let Part = 260
    /// The heads sharing a key/value head: their weighted values (int64).
    [<Literal>]
    let Mix = 324
    /// And their weights' sums (int64).
    [<Literal>]
    let Sums = 580
    /// Two entries of a table.
    [<Literal>]
    let Entries = 596
    /// A gain of RMS norm.
    [<Literal>]
    let Gain = 600
    /// A position's record in the cache: keys, then values, int16.
    [<Literal>]
    let Record = 604
    /// The tokenizer's: two pieces joined, a piece, a number, a space.
    [<Literal>]
    let Joined = 860
    [<Literal>]
    let Piece = 924
    [<Literal>]
    let Number = 956
    [<Literal>]
    let Space = 958
    [<Literal>]
    let End = 1024

// ---- arithmetic -------------------------------------------------------------

/// value, within -limit .. limit.
let inline private clamp limit (value: int) =
    if value > limit then limit elif value < -limit then -limit else value

/// floor(a * b / 2^s) for b in 16 bits, exactly as in 64 bits, from two
/// 32-bit multiplications: a = high * 2^16 + low, so a * b = high * b * 2^16
/// + low * b. (A 64-bit multiplication takes about 20,000 cycles on a 6502.)
let mulShift (a: int) (b: int) (s: int) =
    let low = uint32 (a &&& 0xFFFF) * uint32 b
    let high = (a >>> 16) * b
    if s >= 16 then (high + int (low >>> 16)) >>> (s - 16)
    else (high <<< (16 - s)) + int (low >>> s)

/// floor(a * b / 2^16) for two Q16.16 numbers.
let mulQ16 (a: int) (b: int) = a * (b >>> 16) + mulShift a (b &&& 0xFFFF) 16

/// value * 2^-shift, rounded down: a row's scale, a power of two.
let scaled (value: int) (shift: int) =
    if shift >= 0 then value >>> shift else value <<< -shift

/// Q16.16 to Q8.8, rounded, within 16 bits.
let narrow (value: int) = clamp 32767 ((value + 128) >>> 8)

// ---- the kernels ------------------------------------------------------------

/// A row of weights in the REU: k (its scale is 2^-k), then the int8.
[<Literal>]
let RowHeader = 1

/// One row at at times xq (scale 2^e / 65536), as Q16.16: the row's scale
/// and the vector's are powers of two, so the sum only shifts.
let private rowTimes (at: int<reu>) (cols: int) (xq: nativeptr<sbyte>) (e: int) =
    let row = high<byte> Map.Row
    fetch at row (cols + RowHeader)
    scaled (dot (cast<sbyte> row RowHeader) xq cols) (int (get row 0) - e)

/// out[r] = row r . xq, for the rows x cols matrix at at, one row after
/// the other.
let matVecRows (at: int<reu>) (rows: int) (cols: int) (xq: nativeptr<sbyte>) (e: int) (out: nativeptr<int>) =
    for r in 0 .. rows - 1 do
        set out r (rowTimes (at +. r * (cols + RowHeader)) cols xq e)

/// The row whose product with xq is largest (the first if several are):
/// the classifier, without keeping its results.
let classify (at: int<reu>) (rows: int) (cols: int) (xq: nativeptr<sbyte>) (e: int) =
    let mutable best = 0
    let mutable largest = 0
    for r in 0 .. rows - 1 do
        let value = rowTimes (at +. r * (cols + RowHeader)) cols xq e
        if r = 0 || value > largest then
            largest <- value
            best <- r
    best

/// v (Q16.16) rounded into q, int8 with one power-of-two scale; returns e,
/// v[i] ~ q[i] * 2^e / 65536.
let quantize (v: nativeptr<int>) (n: int) (q: nativeptr<sbyte>) =
    let mutable largest = 0
    for i in 0 .. n - 1 do
        let a = abs (get v i)
        if a > largest then largest <- a
    let mutable bits = 0
    while largest > 0 do
        bits <- bits + 1
        largest <- largest >>> 1
    let e = bits - 7
    for i in 0 .. n - 1 do
        let r = if e > 0 then (get v i + (1 <<< (e - 1))) >>> e else get v i <<< -e
        set q i (sbyte (clamp 127 r))
    e

/// a * a, exactly, from 16-bit pieces: a = h * 2^16 + l, so a^2 = h^2 * 2^32
/// + 2hl * 2^16 + l^2.
let private square (a: int) =
    let a = abs a
    let h = uint32 (a >>> 16)
    let l = uint32 (a &&& 0xFFFF)
    (int64 (h * h) <<< 32) + (int64 (h * l) <<< 17) + int64 (l * l)

let private isqrt (n: int64) =
    let mutable rest = n
    let mutable root = 0L
    let mutable bit = 1L <<< 62
    while bit > n do
        bit <- bit >>> 2
    while bit <> 0L do
        if rest >= root + bit then
            rest <- rest - (root + bit)
            root <- (root >>> 1) + bit
        else
            root <- root >>> 1
        bit <- bit >>> 2
    root

/// out = x * gains / rms(x), all Q16.16, with the n gains (int32) in the REU
/// at gains: the mean square is Q32.32 plus 1e-5, its integer square root
/// Q16.16, and the division a multiplication by 2^32 / rms.
let rmsNorm (out: nativeptr<int>) (x: nativeptr<int>) (gains: int<reu>) (n: int) =
    let mutable squares = 0L
    for i in 0 .. n - 1 do
        squares <- squares + square (get x i)
    let rms = isqrt (squares / int64 n + 42950L)
    let inverse = (1L <<< 32) / (if rms < 1L then 1L else rms)
    let r = if inverse > 0x7FFFFFFFL then 0x7FFFFFFF else int inverse
    let gain = high<byte> Map.Gain
    for i in 0 .. n - 1 do
        fetch (gains +. i * 4) gain 4
        set out i (mulQ16 (mulQ16 (get x i) (getInt gain 0)) r)

/// Entry i of a table in the REU (uint16), and the way f / 256 to the next.
let private lookup (table: int<reu>) (i: int) (f: int) =
    let entries = high<byte> Map.Entries
    fetch (table +. i * 2) entries 4
    let a = getU16 entries 0
    a + (((getU16 entries 2 - a) * f) >>> 8)

/// exp(-d) for d in Q8.8 shifted left 8, as Q1.15, from the table of
/// exp(-i/16).
let private expNeg (table: int<reu>) (d: int) =
    if d >= (256 <<< 12) then 0 else lookup table (d >>> 12) ((d >>> 4) &&& 0xFF)

/// sigmoid(a) for a in Q16.16, as Q1.15.
let private sigmoid (table: int<reu>) (a: int) =
    if a < -(8 <<< 16) then 0
    elif a >= (8 <<< 16) then 32768
    else lookup table ((a >>> 12) + 128) ((a >>> 4) &&& 0xFF)

/// Narrows q (dim) and k (kvDim) from Q16.16 to Q8.8, as attention and the
/// cache keep them, then rotates them by this position's angles: table holds
/// cos, sin (Q1.14, int16) for each pair of channels in a head.
let rope (q: nativeptr<int>) (k: nativeptr<int>) (dim: int) (kvDim: int) (headSize: int) (table: nativeptr<byte>) =
    for i in 0 .. 2 .. dim - 1 do
        let pair = (i % headSize) / 2
        let c = getI16 table (pair * 4)
        let s = getI16 table (pair * 4 + 2)
        let q0 = narrow (get q i)
        let q1 = narrow (get q (i + 1))
        set q i ((q0 * c - q1 * s) >>> 14)
        set q (i + 1) ((q0 * s + q1 * c) >>> 14)
        if i < kvDim then
            let k0 = narrow (get k i)
            let k1 = narrow (get k (i + 1))
            set k i ((k0 * c - k1 * s) >>> 14)
            set k (i + 1) ((k0 * s + k1 * c) >>> 14)

/// One layer's attention for the query q (Q8.8, from rope) over positions
/// 0..position of the cache at cache, a key/value head at a time with the
/// kvMul query heads that share it. shape: heads, kvMul, headSize, kvDim,
/// seqLen. A position's record in the cache is its keys, then its values,
/// kvDim int16 (Q8.8) each. Scores are q . k / sqrt(8) (23170 / 65536), kept
/// as Q8.8; each becomes its softmax weight exp(score - max), Q1.15; out =
/// the weighted values / the weights' sum, Q16.16.
let attention (shape: nativeptr<int>) (q: nativeptr<int>) (cache: int<reu>) (position: int) (scores: nativeptr<int16>) (exp: int<reu>) (out: nativeptr<int>) =
    let heads, kvMul, headSize, kvDim, seqLen = get shape 0, get shape 1, get shape 2, get shape 3, get shape 4
    let kvRecord = kvDim * 4
    let part = high<byte> Map.Part
    let mix = high<int64> Map.Mix
    let sums = high<int64> Map.Sums
    for kh in 0 .. heads / kvMul - 1 do
        let keys = cache +. kh * headSize * 2
        for t in 0 .. position do
            fetch (keys +. t * kvRecord) part (headSize * 2)
            for hh in 0 .. kvMul - 1 do
                let h = (kh * kvMul + hh) * headSize
                let mutable sum = 0L
                for j in 0 .. headSize - 1 do
                    sum <- sum + int64 (get q (h + j) * getI16 part (j * 2))
                let score = mulShift (int (sum >>> 8)) 23170 16
                set scores (hh * seqLen + t) (int16 (clamp 32767 score))
        for hh in 0 .. kvMul - 1 do
            let at = hh * seqLen
            let mutable largest = int (get scores at)
            for t in 1 .. position do
                let s = int (get scores (at + t))
                if s > largest then largest <- s
            let mutable sum = 0L
            for t in 0 .. position do
                let w = expNeg exp ((largest - int (get scores (at + t))) <<< 8)
                set scores (at + t) (int16 (uint16 w))
                sum <- sum + int64 w
            set sums hh sum
            for j in 0 .. headSize - 1 do
                set mix (hh * headSize + j) 0L
        let values = keys +. kvDim * 2
        for t in 0 .. position do
            fetch (values +. t * kvRecord) part (headSize * 2)
            for hh in 0 .. kvMul - 1 do
                match int (uint16 (get scores (hh * seqLen + t))) with
                | 0 -> ()
                | w ->
                    for j in 0 .. headSize - 1 do
                        let at = hh * headSize + j
                        set mix at (get mix at + int64 (w * getI16 part (j * 2)))
        for j in 0 .. kvMul * headSize - 1 do
            set out (kh * kvMul * headSize + j) (int ((get mix j <<< 8) / get sums (j / headSize)))

/// The feed-forward block's gate: hb[r] = silu(w1 row r . xq) * (w3 row r .
/// xq), Q16.16, for the rows x cols matrices w1 and w3.
let gate (w1: int<reu>) (w3: int<reu>) (rows: int) (cols: int) (xq: nativeptr<sbyte>) (e: int) (table: int<reu>) (hb: nativeptr<int>) =
    for r in 0 .. rows - 1 do
        let a = rowTimes (w1 +. r * (cols + RowHeader)) cols xq e
        let b = rowTimes (w3 +. r * (cols + RowHeader)) cols xq e
        set hb r (mulQ16 (mulShift a (sigmoid table a) 15) b)

/// The embedding row at at (k, then dim int8) into x, Q16.16.
let embed (at: int<reu>) (x: nativeptr<int>) (dim: int) =
    let row = high<byte> Map.Row
    fetch at row (dim + RowHeader)
    let shift = int (get row 0) - 16
    let weights = cast<sbyte> row RowHeader
    for i in 0 .. dim - 1 do
        set x i (scaled (int (get weights i)) shift)

/// k (Q8.8, from rope) and v (Q16.16, narrowed to Q8.8) into the cache at at.
let cache (k: nativeptr<int>) (v: nativeptr<int>) (kvDim: int) (at: int<reu>) =
    let record = high<int16> Map.Record
    for j in 0 .. kvDim - 1 do
        set record j (int16 (get k j))
        set record (kvDim + j) (int16 (narrow (get v j)))
    store (NativePtr.ofNativeInt (NativePtr.toNativeInt record)) at (kvDim * 4)

/// x += y.
let add (x: nativeptr<int>) (y: nativeptr<int>) (n: int) =
    for i in 0 .. n - 1 do
        set x i (get x i + get y i)

// ---- the tokenizer ----------------------------------------------------------
//
// llama2.c's, with its scores turned into ranks. Its block in the REU:
//   offsets  uint16 per token, into the pieces
//   lengths  byte per token
//   ranks    int16 per token (higher merges first)
//   table    HashSize uint16: id + 1 by hash of the piece
//   pieces   the bytes

[<Literal>]
let private Vocab = 512

[<Literal>]
let private HashSize = 1024

let mutable private start = 0<reu>

/// The uint16 in the REU at at.
let private wordAt (at: int<reu>) =
    let number = high<byte> Map.Number
    fetch at number 2
    getU16 number 0

let private offsetOf id = start +. (Vocab * 5 + HashSize * 2 + wordAt (start +. id * 2))
let private lengthOf id = wordAt (start +. (Vocab * 2 + id)) &&& 0xFF
let private rankOf id = wordAt (start +. (Vocab * 3 + id * 2))
let private slotAt slot = wordAt (start +. (Vocab * 5 + slot * 2))

/// The hash of length bytes at p (tool/Pack.fs builds the table with it).
let private hash (p: nativeptr<byte>) (length: int) =
    let mutable h = 0
    for i in 0 .. length - 1 do
        h <- (h * 33 + int (get p i)) &&& 0xFFFF
    h &&& (HashSize - 1)

/// Whether piece id is exactly the length bytes at p.
let private same (id: int) (p: nativeptr<byte>) (length: int) =
    if lengthOf id <> length || length > 32 then false
    else
        let piece = high<byte> Map.Piece
        fetch (offsetOf id) piece length
        let mutable i = 0
        while i < length && get piece i = get p i do
            i <- i + 1
        i = length

/// The id of the piece with these length bytes at p, or -1. The table is
/// open-addressed: from the hash on, slot after slot until an empty one.
let private find (p: nativeptr<byte>) (length: int) =
    let mutable slot = hash p length
    let mutable entry = slotAt slot
    while entry <> 0 && not (same (entry - 1) p length) do
        slot <- (slot + 1) &&& (HashSize - 1)
        entry <- slotAt slot
    entry - 1

/// The length bytes at p are a piece: its id.
[<return: Struct>]
let inline private (|Piece|_|) (length: int) (p: nativeptr<byte>) =
    match find p length with
    | -1 -> ValueNone
    | id -> ValueSome id

/// Text (length bytes) to ids, as llama2.c's encode: BOS, a space piece,
/// a piece per byte (or the byte + 3), then repeatedly the adjacent pair
/// whose joined piece ranks highest is merged. ids needs room for
/// length + 2. Returns the count. (llama2.c takes a UTF-8 character at a
/// time; the same for ASCII, which is all a C64 types.)
let encode (tokenizer: int<reu>) (text: nativeptr<byte>) (length: int) (ids: nativeptr<int>) =
    start <- tokenizer
    let space = high<byte> Map.Space
    set space 0 ' 'B
    set ids 0 1 // BOS
    let mutable count = 1
    if length > 0 then
        match space with
        | Piece 1 id ->
            set ids count id
            count <- count + 1
        | _ -> ()
    for i in 0 .. length - 1 do
        set ids count (match NativePtr.add text i with
                       | Piece 1 id -> id
                       | _ -> int (get text i) + 3)
        count <- count + 1

    let joined = high<byte> Map.Joined
    let mutable merging = true
    while merging do
        let mutable bestRank = -1
        let mutable best = -1
        let mutable bestId = -1
        for p in 1 .. count - 2 do
            let a = get ids p
            let b = get ids (p + 1)
            let la = lengthOf a
            let lb = lengthOf b
            let both = la + lb
            if both <= 64 then
                fetch (offsetOf a) joined la
                fetch (offsetOf b) (NativePtr.add joined la) lb
                match joined with
                | Piece both id ->
                    let rank = rankOf id
                    if rank > bestRank then
                        bestRank <- rank
                        best <- p
                        bestId <- id
                | _ -> ()
        if best < 0 then
            merging <- false
        else
            set ids best bestId
            for p in best + 1 .. count - 2 do
                set ids p (get ids (p + 1))
            count <- count - 1
    count

/// A hex digit's value; something else, -100.
let private hexDigit (c: byte) =
    match c with
    | c when c >= '0'B && c <= '9'B -> int c - int '0'B
    | c when c >= 'A'B && c <= 'F'B -> int c - int 'A'B + 10
    | c when c >= 'a'B && c <= 'f'B -> int c - int 'a'B + 10
    | _ -> -100

/// "<0x0A>" at at in out: a piece that stands for one byte, the byte.
[<return: Struct>]
let inline private (|ByteToken|_|) (length: int) (at: int) (out: nativeptr<byte>) =
    if length = 6 && get out at = '<'B && get out (at + 1) = '0'B && get out (at + 2) = 'x'B then
        ValueSome(byte (hexDigit (get out (at + 3)) * 16 + hexDigit (get out (at + 4))))
    else ValueNone

/// The text of token (after previous) into out, which needs room for 32
/// bytes; returns its length. No leading space right after BOS, and the
/// byte itself for a piece like "<0x0A>".
let decode (tokenizer: int<reu>) (previous: int) (token: int) (out: nativeptr<byte>) =
    start <- tokenizer
    let length = lengthOf token
    fetch (offsetOf token) out length
    let at = if previous = 1 && get out 0 = ' 'B then 1 else 0
    let rest = length - at
    match out with
    | ByteToken rest at b ->
        set out 0 b
        1
    | _ ->
        for i in 0 .. rest - 1 do
            set out i (get out (at + i))
        rest
