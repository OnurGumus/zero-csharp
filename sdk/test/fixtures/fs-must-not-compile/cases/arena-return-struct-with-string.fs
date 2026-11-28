// expect: FS0041
// Nor a struct holding a string: the struct is copied out, the string isn't.
[<Struct>]
type Named = { Name: string; Score: int }
let frame = Arena.Create(4096un, "frame")
let leak () =
    arena frame {
        return { Name = string 42; Score = 1 }
    }
