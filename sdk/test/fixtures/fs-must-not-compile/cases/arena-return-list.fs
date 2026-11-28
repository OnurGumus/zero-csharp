// expect: FS0041
// arena { } can't return a list: its cells are in the arena, freed as it ends.
let frame = Arena.Create(4096un, "frame")
let leak () =
    arena frame {
        let xs = [ 1; 2; 3 ]
        return xs
    }
