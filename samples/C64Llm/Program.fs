// Example: a language model on the Commodore 64, in F#. Type the start of a
// story and the model goes on with it, a token at a time; an empty line lets
// it begin one of its own.
//
// The model is stories260K (about 260,000 parameters, trained on children's
// stories), in an REU: Model.fs says how it runs in integers only, and
// tool/ makes the REU image and checks the C64's arithmetic on the host.
// Build and run: samples/C64Llm/README.md.
//
// A line like /20 sets how many tokens a story has at most, the prompt's
// included (default: 128, all the model can hold). Each story starts afresh.
//
// The unions here are structs: a value of one lives in registers and on the
// stack, never on the heap, which on a C64 has a few KB to spare. The
// functions that make one are inline, so the compiler sees each value made
// and matched in the same place and keeps only the branch it takes. (Not
// inline, the "/20" pattern alone was 900 bytes of 6502 code.)
module Llm64.Program

open System
open FSharp.NativeInterop
open Llm64.Machine
open Llm64.Model

#nowarn "9" // native pointers (Machine.fs says why)

[<Literal>]
let LineLength = 40

/// The token that starts a story, and ends one when the model says it.
[<Literal>]
let Bos = 1

// The program's buffers, after the model's in the RAM under the KERNAL ROM:
// a typed line, its bytes, its tokens, and a token's text.
[<Literal>]
let private Line = Layout.End

[<Literal>]
let private Text = Line + LineLength * 2

[<Literal>]
let private Ids = Text + LineLength

[<Literal>]
let private Piece = Ids + (LineLength + 2) * 4

let line: nativeptr<char> = high Line
let text: nativeptr<byte> = high Text
let ids: nativeptr<int> = high Ids
let piece: nativeptr<byte> = high Piece

let print (s: string) = Console.Write s

/// n (0 or more) in decimal. Console.Write(n) would bring in .NET's number
/// formatting, 1.7 KB of 6502 code for the three numbers this prints.
let rec printNumber (n: int) =
    if n >= 10 then printNumber (n / 10)
    Console.Write(char (int '0' + n % 10))

// ---- what a typed line asks for ---------------------------------------------

/// A line typed at the prompt.
[<Struct>]
type Command =
    /// The input is over.
    | Quit
    | TooLong
    /// "/20": stories of 20 tokens at most. (-1: "/" without a number,
    /// which just shows the limit.)
    | SetLimit of limit: int
    /// A story's start (length bytes in text; none: the model's own start).
    | Story of length: int

[<return: Struct>]
let inline (|Digit|_|) (c: byte) =
    if c >= '0'B && c <= '9'B then ValueSome(int (c - '0'B)) else ValueNone

/// A line of length bytes in text that starts with "/": the number after
/// it, or -1 if what follows isn't one.
[<return: Struct>]
let inline (|Slash|_|) (length: int) =
    if length > 0 && get text 0 = '/'B then
        let mutable n = if length > 1 then 0 else -1
        for i in 1 .. length - 1 do
            match get text i with
            | Digit d when n >= 0 -> n <- n * 10 + d
            | _ -> n <- -1
        ValueSome n
    else ValueNone

let inline read () =
    match In.ReadLine(Span<char>(NativePtr.toVoidPtr line, LineLength)) with
    | length when length < 0 -> Quit
    | length when length > LineLength -> TooLong
    | length ->
        for i in 0 .. length - 1 do
            set text i (byte (get line i))
        match length with
        | Slash limit -> SetLimit limit
        | _ -> Story length

// ---- telling a story ---------------------------------------------------------

/// What follows the token at a position.
[<Struct>]
type Next =
    /// The prompt's next token: the model reads it, and says nothing.
    | FromPrompt of prompt: int
    /// The model's own choice.
    | Said of said: int
    | TheEnd

/// The prompt is count ids long; likeliest is the model's choice after
/// position.
let inline next count position likeliest =
    if position + 1 < count then FromPrompt(get ids (position + 1))
    else
        match likeliest with
        | Bos -> TheEnd
        | token -> Said token

/// Prints token, which follows previous.
let say previous token =
    let n = Tokenizer.decode previous token piece
    for i in 0 .. n - 1 do
        Console.Write(char (get piece i))
    // Each word as it comes: Out keeps text until 256 bytes are waiting.
    Out.Flush()

/// Goes on from token at position until the model ends the story or it has
/// limit tokens; returns how many tokens the model said.
let rec tell count limit token position said =
    if position + 1 >= limit then said
    else
        match next count position (Llm.next token position) with
        | TheEnd -> said
        | FromPrompt prompt -> tell count limit prompt (position + 1) said
        | Said token' ->
            say token token'
            tell count limit token' (position + 1) (said + 1)

/// Tells a story from the length bytes in text, up to limit tokens.
let story length limit =
    let started = clock ()
    let said = tell (Tokenizer.encode text length ids) limit Bos 0 0
    let tenths = clock () - started
    print "\n("
    printNumber said
    print " tokens, "
    printNumber (tenths / 10)
    print " s)\n"

// ---- the session -------------------------------------------------------------

let rec session limit =
    print "\n> "
    match read () with
    | Quit -> 0
    | TooLong ->
        print "(40 characters at most)\n"
        session limit
    | SetLimit n ->
        let limit = if n > 1 then min n Shape.SeqLen else limit
        print "tokens: "
        printNumber limit
        print "\n"
        session limit
    | Story length ->
        story length limit
        session limit

[<EntryPoint>]
let main _ =
    print "STORIES260K ON A C64\n"
    // With no REU, or another image in it, load says false.
    if Llm.load () then
        Tokenizer.load ()
        print "Type the start of a story.\n"
        session Shape.SeqLen
    else
        print "This needs an REU (512 KB or more) holding\nstories260K.reu, packed by this version\nof tool/.\n"
        1
