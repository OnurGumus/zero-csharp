// An F# program using an F# library (Shapes): built for zero mode and for
// .NET, it must print the same after the first line.
open System.Threading.Tasks
open Shapes

#if ZERO_DOTNET
printfn "on .NET"
#else
printfn "on zero"
#endif

// The library's task, awaited here: one Task type for both.
let total: Task<int> = totalArea [ Circle 1; Square 2; Circle 3 ]
printfn "total %d" total.Result

// Task.Run from the program too, on lib/'s fibers.
let squares = [| for i in 1 .. 8 -> Task.Run(fun () -> i * i) |]
let mutable sum = 0
for t in squares do
    sum <- sum + t.Result
printfn "squares %d" sum

// zero's MailboxProcessor.
let counter =
    MailboxProcessor<int * AsyncReplyChannel<int>>.Start(fun inbox ->
        let rec loop n =
            async {
                let! (k, reply) = inbox.Receive()
                reply.Reply(n + k)
                return! loop (n + k)
            }
        loop 0)
counter.PostAndReply(fun r -> (5, r)) |> ignore
printfn "agent %d" (counter.PostAndReply(fun r -> (7, r)))
