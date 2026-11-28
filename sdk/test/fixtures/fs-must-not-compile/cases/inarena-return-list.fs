// expect: FS0041
// Async.InArena can't return a list either: its cells are in the arena.
let frame = Arena.Create(4096un, "frame")
let leak () = Async.InArena(frame, async { return [ 1; 2; 3 ] })
