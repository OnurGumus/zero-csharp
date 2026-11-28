// Arena.DebugChecks and agents: a message made inside an arena scope may be
// posted from it if the agent takes it before the scope ends; one still
// queued when the scope ends stops the program, which otherwise would let the
// agent read freed memory later.
Arena.DebugChecks <- true

type Msg =
    | Add of int
    | Total of AsyncReplyChannel<int>

let frame = Arena.Create(4096un, "frame")

let counter =
    MailboxProcessor.Start(fun inbox ->
        let rec loop (total: int) : Async<unit> =
            async {
                let! msg = inbox.Receive()
                match msg with
                | Add n -> return! loop (total + n)
                | Total reply ->
                    reply.Reply total
                    return! loop total
            }
        loop 0)

// Posted inside, taken inside: the scope waits for the agent's reply.
[<Safety "the agent has taken the message before the scope ends">]
let waitsForIt () =
    Async.InArena(frame, async {
        counter.Post(Add 1)
        let! total = counter.PostAndAsyncReply Total
        return total })

printfn "taken inside: %d" (Async.RunSynchronously(waitsForIt ()))

// Posted inside and left queued: the check stops the program here.
[<Safety "the mistake this test is about">]
let leavesIt () =
    Async.InArena(frame, async {
        counter.Post(Add 2)
        return 0 })

printfn "posting and leaving"
Async.RunSynchronously(leavesIt ()) |> ignore
printfn "not here"
