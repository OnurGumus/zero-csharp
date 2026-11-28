// Sequence expressions, comprehensions and the Seq, List, Array, Map, Set and
// String modules: each line is what .NET's FSharp.Core prints (expected.txt).
let show (items: seq<'T>) = "[" + String.concat "; " (Seq.map string items) + "]"
let pair (a, b) = "(" + string a + ", " + string b + ")"
let opt (o: 'T option) = match o with Some x -> "Some " + string x | None -> "None"

// Sequence expressions
// docs:seq
type Resource(name: string) =
    interface System.IDisposable with
        member _.Dispose() = printfn "dispose %s" name

let rec countdown n = seq {
    if n > 0 then
        yield n
        yield! countdown (n - 1) }

let numbers = seq {
    yield 1
    yield! [ 2; 3 ]
    for i in 4 .. 5 do
        yield i
    let mutable i = 6
    while i <= 7 do
        yield i
        i <- i + 1
    use r = new Resource("r")
    try
        yield 8
    finally
        printfn "finally" }
// docs:end

printfn "%s" (show numbers)
printfn "%s" (show (countdown 3))
printfn "%s" (show (Seq.take 3 numbers))
printfn "%s" (show [ for x in 1 .. 3 do for y in 1 .. 2 -> x * 10 + y ])
printfn "%s" (show [| for i in 10 .. -3 .. 1 -> i |])
printfn "%s" (show [ yield 0; yield! [ 1; 2 ]; for i in 3 .. 4 do if i % 2 = 0 then yield i ])
printfn "%s" (show [ 1 .. 5 ])
printfn "%s" (show [| 'a' .. 'e' |])

// Seq
let s = seq { 5; 3; 8; 1; 9; 2; 8 }
printfn "%s" (show (Seq.map (fun x -> x * 2) s))
printfn "%s" (show (Seq.filter (fun x -> x > 4) s))
printfn "%s" (show (Seq.choose (fun x -> if x % 2 = 0 then Some(x * 100) else None) s))
printfn "%s %s %s" (show (Seq.skip 2 s)) (show (Seq.truncate 10 s)) (show (Seq.takeWhile (fun x -> x > 2) s))
printfn "%s" (show (Seq.skipWhile (fun x -> x > 2) s))
printfn "%s" (show (Seq.map pair (Seq.pairwise [ 1; 2; 3 ])))
printfn "%s" (show (Seq.map show (Seq.windowed 3 [ 1; 2; 3; 4 ])))
printfn "%s" (show (Seq.map show (Seq.chunkBySize 3 [ 1 .. 7 ])))
printfn "%s %s" (show (Seq.distinct s)) (show (Seq.sort s))
printfn "%s" (show (Seq.sortDescending s))
printfn "%s" (show (Seq.sortBy (fun x -> -x) s))
printfn "%s" (show (Seq.map (fun (k, xs) -> string k + ":" + show xs) (Seq.groupBy (fun x -> x % 3) s)))
printfn "%s" (show (Seq.map pair (Seq.countBy (fun x -> x > 4) s)))
printfn "%s %s" (show (Seq.rev s)) (show (Seq.map pair (Seq.zip [ 1; 2 ] [ "a"; "b"; "c" ])))
printfn "%s" (show (Seq.map pair (Seq.indexed [ "x"; "y" ])))
printfn "%s" (show (Seq.scan (+) 0 [ 1; 2; 3 ]))
printfn "%d %d %d" (Seq.fold (+) 0 s) (Seq.reduce max s) (Seq.length s)
printfn "%b %b %b" (Seq.exists (fun x -> x = 9) s) (Seq.forall (fun x -> x > 0) s) (Seq.isEmpty Seq.empty<int>)
printfn "%s %d %d" (opt (Seq.tryFind (fun x -> x > 100) s)) (Seq.find (fun x -> x > 5) s) (Seq.pick (fun x -> if x > 5 then Some(x + 1) else None) s)
printfn "%d %d %d %d" (Seq.head s) (Seq.last s) (Seq.item 2 s) (Seq.exactlyOne [ 42 ])
printfn "%s" (show (Seq.init 4 (fun i -> i * i)))
printfn "%s" (show (Seq.take 5 (Seq.initInfinite (fun i -> i * 3))))
printfn "%s" (show (Seq.unfold (fun (a, b) -> if a > 50 then None else Some(a, (b, a + b))) (0, 1)))
let mutable reads = 0
let cached = Seq.cache (seq { for i in 1 .. 3 do reads <- reads + 1; yield i })
printfn "%d %d %d" (Seq.sum cached) (Seq.sum cached) reads
printfn "%s" (show (Seq.append [ 1; 2 ] [ 3 ]))
printfn "%s" (show (Seq.concat [ [ 1 ]; [ 2; 3 ]; [] ]))
printfn "%s" (show (Seq.collect (fun x -> [ x; x ]) [ 1; 2 ]))
printfn "%d %d %d %d" (Seq.min s) (Seq.max s) (Seq.minBy (fun x -> -x) s) (Seq.maxBy (fun x -> x % 5) s)
printfn "%g %g %d" (Seq.average [ 1.0; 2.0; 4.5 ]) (Seq.averageBy float [ 1; 2 ]) (Seq.sumBy (fun x -> x * x) [ 1; 2; 3 ])
printfn "%s" (show (Seq.except [ 8; 1 ] s))
printfn "%s" (show (Seq.map pair (Seq.allPairs [ 1; 2 ] [ 'a'; 'b' ])))
let mapped, total = Seq.mapFold (fun acc x -> (x * 2, acc + x)) 0 [ 1; 2; 3 ]
printfn "%s %d" (show mapped) total
printfn "%s" (show (Seq.map show (Seq.splitInto 3 [ 1 .. 7 ])))
printfn "%s" (show (Seq.map show (Seq.transpose [ [ 1; 2; 3 ]; [ 4; 5; 6 ] ])))
printfn "%d %d" (Seq.compareWith compare [ 1; 2 ] [ 1; 3 ]) (Seq.compareWith compare [ 1; 2 ] [ 1 ])
printfn "%s" (show (Seq.distinctBy (fun x -> x % 3) s))

// List
let l = [ 4; 1; 3; 1; 5 ]
printfn "%s %s" (show (List.sort l)) (show (List.sortDescending l))
printfn "%s" (show (List.map (fun x -> x + 1) l))
printfn "%s" (show (List.mapi (fun i x -> i * x) l))
printfn "%s %s" (show (List.filter (fun x -> x > 2) l)) (show (List.rev l))
let evens, odds = List.partition (fun x -> x % 2 = 0) l
printfn "%s %s" (show evens) (show odds)
printfn "%d %d %d %d" (List.sum l) (List.max l) (List.min l) (List.length l)
printfn "%d %d" (List.head l) (List.last l)
printfn "%s" (show (List.tail l))
printfn "%s %s" (show (List.take 2 l)) (show (List.skip 3 l))
printfn "%s" (show (List.distinct l))
printfn "%s" (show (List.collect (fun x -> [ x; -x ]) [ 1; 2 ]))
printfn "%s" (show (List.append [ 1 ] [ 2; 3 ] @ [ 4 ]))
printfn "%s" (show (List.init 3 (fun i -> i + 10)))
printfn "%d" (List.fold (fun acc x -> acc * 10 + x) 0 [ 1; 2; 3 ])
printfn "%s" (List.foldBack (fun x acc -> string x + acc) [ 1; 2; 3 ] "")
printfn "%s" (show (List.map pair (List.zip [ 1; 2 ] [ "a"; "b" ])))
let ns, ss = List.unzip [ (1, "a"); (2, "b") ]
printfn "%s %s" (show ns) (show ss)
printfn "%b %b" (List.contains 3 l) (List.contains 7 l)
printfn "%s %s" (opt (List.tryFindIndex (fun x -> x = 3) l)) (opt (List.tryItem 10 l))
printfn "%s" (show (List.map (fun (k, xs) -> string k + ":" + show xs) (List.groupBy (fun x -> x % 2) l)))
printfn "%s" (show (List.sortBy (fun (s: string) -> s.Length) [ "ccc"; "a"; "bb"; "d" ]))
printfn "%s" (show (List.sortWith (fun a b -> compare b a) l))
printfn "%s" (show (List.choose (fun x -> if x > 3 then Some(string x) else None) l))
printfn "%s" (show (List.map show (List.chunkBySize 2 l)))
printfn "%s" (show (List.map show (List.windowed 2 [ 1; 2; 3 ])))
printfn "%s" (show (List.pairwise [ 1; 2; 3 ] |> List.map pair))
printfn "%s" (show (List.scan (+) 0 [ 1; 2; 3 ]))
printfn "%s" (show (List.replicate 3 "x"))
printfn "%s" (show (List.ofSeq (seq { 1 .. 3 })))
printfn "%s" (show (List.ofArray [| 7; 8 |]))
let before, after = List.splitAt 2 l
printfn "%s %s" (show before) (show after)
printfn "%s" (show (List.except [ 1 ] l))
printfn "%g %d" (List.average [ 1.0; 2.0 ]) (List.sumBy String.length [ "ab"; "c" ])
printfn "%s" (show (List.indexed [ 'a'; 'b' ] |> List.map pair))
printfn "%s" (show (List.truncate 2 l))
printfn "%s" (show (List.skipWhile (fun x -> x > 2) l))
printfn "%s" (show (List.takeWhile (fun x -> x > 2) l))

// Array
let a = [| 5; 2; 7; 1 |]
let sorted = Array.copy a
Array.sortInPlace sorted
printfn "%s %s" (show a) (show sorted)
printfn "%s %s" (show (Array.sub a 1 2)) (show (Array.append a [| 0 |]))
let target = Array.zeroCreate<int> 6
Array.blit a 0 target 1 4
printfn "%s" (show target)
Array.fill target 0 2 9
printfn "%s" (show target)
printfn "%s %s" (show (Array.map (fun x -> x * x) a)) (show (Array.filter (fun x -> x > 2) a))
printfn "%s %s" (show (Array.rev a)) (show (Array.sortDescending a))
let big, small = Array.partition (fun x -> x > 3) a
printfn "%s %s" (show big) (show small)
printfn "%d %d %d" (Array.sum a) (Array.max a) (Array.fold (+) 0 a)
printfn "%s %d" (opt (Array.tryFind (fun x -> x > 5) a)) (Array.findIndex (fun x -> x = 7) a)
printfn "%s" (show (Array.init 3 (fun i -> string i)))
printfn "%s" (show (Array.collect (fun x -> [| x; x |]) [| 1; 2 |]))
printfn "%s" (show (Array.map pair (Array.zip [| 1; 2 |] [| 'x'; 'y' |])))
printfn "%s" (show (Array.toList a))
printfn "%s" (show (Array.ofList [ 3; 4 ]))
printfn "%s" (show (Array.map (fun (k, xs) -> string k + ":" + show xs) (Array.groupBy (fun x -> x % 2) a)))
printfn "%s" (show (Array.sortBy (fun x -> -x) a))
printfn "%s" (show (Array.distinct [| 1; 1; 2 |]))
printfn "%s" (show (Array.choose (fun x -> if x > 4 then Some x else None) a))
printfn "%s" (show (Array.scan (+) 0 [| 1; 2 |]))
printfn "%s" (show (Array.map show (Array.chunkBySize 3 [| 1 .. 5 |])))
printfn "%s" (show (Array.mapi (fun i x -> i + x) a))
printfn "%b %b" (Array.exists (fun x -> x = 2) a) (Array.forall (fun x -> x > 1) a)
printfn "%s" (show (Array.concat [ [| 1 |]; [| 2; 3 |] ]))
printfn "%s %s" (show (Array.take 2 a)) (show (Array.skip 2 a))
printfn "%d %d" (Array.head a) (Array.last a)
printfn "%g" (Array.average [| 1.0; 4.0 |])

// Map
let m = Map.ofList [ (3, "c"); (1, "a"); (2, "b") ]
printfn "%s" (string m)
printfn "%s %s %s" (Map.find 2 m) (opt (Map.tryFind 9 m)) (opt (m.TryFind 1))
let m2 = m |> Map.add 4 "d" |> Map.remove 1
printfn "%s %d %b %b" (string m2) m2.Count (m2.ContainsKey 1) (Map.containsKey 4 m2)
printfn "%s" (show (Map.toList m |> List.map pair))
printfn "%s" (string (Map.map (fun k v -> v + string k) m))
printfn "%s" (string (Map.filter (fun k _ -> k > 1) m))
printfn "%s" (Map.fold (fun acc k v -> acc + string k + v) "" m)
for KeyValue(k, v) in m do printfn "%d=%s" k v
printfn "%s" m.[3]
printfn "%s" (string (m.Change(1, fun _ -> Some "z")))
printfn "%s" (string (Map.empty<string, int>.Add("one", 1).Add("two", 2)))
printfn "%b %b" (Map.isEmpty Map.empty<int, int>) (m = Map.ofList [ (1, "a"); (2, "b"); (3, "c") ])
printfn "%s" (show (Map.keys m))
printfn "%s" (show (Map.values m))
printfn "%s" (opt (Map.tryFindKey (fun _ v -> v = "b") m))
printfn "%s" (string (Map.ofSeq (seq { for i in 1 .. 5 -> (i % 3, i) })))

// Set
let s1 = Set.ofList [ 3; 1; 4; 1; 5 ]
let s2 = set [ 4; 5; 6 ]
printfn "%s %s" (string s1) (string s2)
printfn "%s %s %s" (string (Set.union s1 s2)) (string (Set.intersect s1 s2)) (string (Set.difference s1 s2))
printfn "%b %b %d" (Set.contains 4 s1) (s1.Contains 9) s1.Count
printfn "%s" (show (Set.toList (Set.add 0 s1)))
printfn "%d %d" (Set.minElement s1) (Set.maxElement s1)
printfn "%b %b" (Set.isSubset (set [ 1; 3 ]) s1) (Set.isSubset s2 s1)
printfn "%s" (string (Set.map (fun x -> x % 3) s1))
printfn "%s" (string (Set.filter (fun x -> x > 2) s1))
printfn "%s" (string (Set.ofSeq "hello"))
printfn "%s" (string (Set.remove 3 s1))

// String
printfn "%s" (String.concat ", " [ "a"; "b"; "c" ])
printfn "%s %s" (String.map (fun c -> if c = 'l' then 'L' else c) "hello") (String.filter (fun c -> c <> 'l') "hello")
printfn "%s %s" (String.collect (fun c -> string c + string c) "abc") (String.init 3 (fun i -> string i))
printfn "%s %d" (String.replicate 3 "ab") (String.length "four")
printfn "%b %b" (String.forall (fun c -> c = 'a') "aaa") (String.exists (fun c -> c = 'z') "abc")
String.iteri (fun i c -> if i < 2 then printfn "%d %c" i c) "xyz"
printfn "%s" (String.mapi (fun i c -> if i % 2 = 0 then c else '_') "abcdef")

// Records with the modules
// docs:inventory
type Item = { Name: string; Price: float; Qty: int }

let items =
    [ { Name = "apple"; Price = 0.5; Qty = 10 }
      { Name = "pear"; Price = 0.75; Qty = 4 }
      { Name = "plum"; Price = 0.25; Qty = 0 } ]

let inStock = items |> List.filter (fun i -> i.Qty > 0)
let worth = inStock |> List.sumBy (fun i -> i.Price * float i.Qty)
printfn "%d in stock, worth %.2f" inStock.Length worth

let byName = items |> List.map (fun i -> i.Name, i) |> Map.ofList
printfn "%s" (String.concat ", " (Map.keys byName))
match Map.tryFind "pear" byName with
| Some pear -> printfn "pears: %d" pear.Qty
| None -> printfn "no pears"
// docs:end

// ToString of the core types (FSharp.Core writes these itself)
printfn "%s | %s | %s | %s" (string (Some 1)) (string [ 1; 2; 3; 4 ]) (string (ValueSome 2)) (string [ "a"; "b" ])

// Option and ValueOption
let half x = if x % 2 = 0 then Some(x / 2) else None
printfn "%d %d %b" (Option.defaultValue 0 (half 8)) (half 7 |> Option.defaultWith (fun () -> -1)) (Option.isSome (half 2))
printfn "%d" (half 8 |> Option.map (fun h -> h * 10) |> Option.bind half |> Option.toList |> List.length)
printfn "%d %d" (Option.count (half 3)) (Option.fold (+) 1 (half 4))
printfn "%b %b" (Option.exists (fun h -> h > 1) (half 6)) (Option.contains 3 (half 6))
Option.iter (fun h -> printfn "iter %d" h) (half 10)
let vhalf x = if x % 2 = 0 then ValueSome(x / 2) else ValueNone
printfn "%d %d" (ValueOption.defaultValue 0 (vhalf 8)) (vhalf 9 |> ValueOption.map (fun h -> h + 1) |> ValueOption.defaultValue -1)
printfn "%b %d" (vhalf 4 |> ValueOption.toOption |> Option.isSome) (Option.toValueOption (Some 5) |> ValueOption.get)

// Active patterns: total, partial (option and voption), multi-case
let (|Doubled|) x = x * 2
let (|Even|_|) x = if x % 2 = 0 then Some(x / 2) else None
[<return: Struct>]
let (|Small|_|) x = if x < 10 then ValueSome x else ValueNone
let (|Neg|NonNeg|) x = if x < 0 then Neg else NonNeg
let (|Low|Mid|High|) x = if x < 10 then Low elif x < 100 then Mid else High
let (|A|B|C|D|E|F|G|) x = match x % 7 with 0 -> A | 1 -> B | 2 -> C | 3 -> D | 4 -> E | 5 -> F | _ -> G

for x in [ -4; 7; 42; 300 ] do
    let (Doubled d) = x
    let even = match x with Even h -> sprintf "half %d" h | _ -> "odd"
    let small = match x with Small s -> sprintf "small %d" s | _ -> "big"
    let sign = match x with Neg -> "negative" | NonNeg -> "not negative"
    let size = match x with Low -> "low" | Mid -> "mid" | High -> "high"
    let seventh = match x with A -> "A" | B -> "B" | C -> "C" | D -> "D" | E -> "E" | F -> "F" | G -> "G"
    printfn "%d: %d, %s, %s, %s, %s, %s" x d even small sign size seventh

// Structural equality, hashing and order of arrays and tuples, also boxed
// inside lists, options, sets and maps.
let squares3 = [| 1; 4; 9 |]
printfn "arrays: %b %b %b %d %d" (squares3 = [| 1; 4; 9 |]) (squares3 = [| 1; 4 |]) ([| "x" |] = [| "x" |]) (compare [| 1; 2 |] [| 1; 3 |]) (compare [| 1; 2; 0 |] [| 9 |])
printfn "array hash: %b, nested: %b, of tuples: %b" (hash [| 1; 2 |] = hash [| 1; 2 |]) ([| [| 1 |] |] = [| [| 1 |] |]) ([| (1, "a") |] = [| (1, "a") |])
printfn "tuples: %b %b %b" ([ (1, "a") ] = [ (1, "a") ]) (Some (1, 2) = Some (1, 2)) (hash [ (1, 2) ] = hash [ (1, 2) ])
printfn "set of tuples: %d, map with tuple keys: %s" (Set.ofList [ (1, 2); (0, 5); (1, 2) ]).Count ((Map.ofList [ ((1, "x"), "one"); ((0, "y"), "zero") ])[(1, "x")])
printfn "sorted: %s" (List.sort [ (2, "b"); (1, "z"); (1, "a") ] |> List.map (fun (n, s) -> sprintf "%d%s" n s) |> String.concat ",")
printfn "sorted arrays: %s" (List.sort [ [| 2 |]; [| 1; 1 |]; [| 1 |] ] |> List.map (fun x -> string x.Length) |> String.concat ",")
