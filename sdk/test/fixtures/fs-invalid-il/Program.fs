// Code the native compiler can't compile: GetType on a span boxes it, which
// is invalid IL. The build must stop with ZERO015, not build a program that
// fails when the method runs.
let typeOf (numbers: int[]) = System.ReadOnlySpan<int>(numbers).GetType()
printfn "%b" (isNull (typeOf [| 1 |]))
