// Example: a paddle-and-ball game for the Commodore 64. You are the left
// paddle, with W and S or a joystick in port 2, against the computer on the
// right; the first to 7 points wins. Wait at the title screen and the
// computer plays itself. M turns the music on and off; Q goes back to the
// title, and from there to BASIC.
// Build: ./build.sh --c64 Paddles    Run: ./run.sh --c64 --window Paddles
//
// The game's state is records, unions say who moves a paddle and what each
// frame of play led to, and switches with patterns take them apart.
//
// Typing T at the title screen plays one match, computer against computer,
// and reports on it. That is how ./test.sh --c64 checks the game: the match
// is the same on every run. Exit code = number of failed checks.

using System;
using System.Runtime.CompilerServices;

namespace PaddlesSample;

// The playfield, in sprite coordinates: the screen's 320x200 pixels start at
// x 24 and y 50.
static class Court
{
    public const int Left = 24, Right = 344, Top = 50, Bottom = 250;
    public const int Middle = (Left + Right) / 2, Center = (Top + Bottom) / 2;
    public const int LeftFace = 40, RightFace = 328;   // where the paddles face the ball
    public const int PaddleHeight = 42;                // a sprite's 21 rows, doubled
    public const int BallSize = 4;
    public const int WinningScore = 7;

    public static int Clamp(int paddleTop) => Math.Clamp(paddleTop, Top, Bottom - PaddleHeight);
}

enum Side { Left, Right }

static class SideExtensions
{
    extension(Side side)
    {
        public Side Opposite => side == Side.Left ? Side.Right : Side.Left;
    }
}

// docs:records
// The ball: its top left corner, and how far it moves each frame.
readonly record struct Ball(int X, int Y, int Dx, int Dy)
{
    public int Middle => Y + Court.BallSize / 2;
    public Ball Moved() => this with { X = X + Dx, Y = Y + Dy };
    public bool Meets(int paddleTop) => Y + Court.BallSize > paddleTop && Y < paddleTop + Court.PaddleHeight;
}

readonly record struct Score(int Left, int Right)
{
    public Score For(Side side) => side == Side.Left ? this with { Left = Left + 1 } : this with { Right = Right + 1 };

    public Side? Winner => Left == Court.WinningScore ? Side.Left
        : Right == Court.WinningScore ? Side.Right
        : null;
}
// docs:end

// docs:unions
// Who moves a paddle, and how fast.
readonly record struct Human(int Speed);
readonly record struct Computer(int Speed);
union Controller(Human, Computer);

// What a frame of play led to.
readonly record struct InPlay();
readonly record struct HitWall();
readonly record struct HitPaddle(Side Side);
readonly record struct Scored(Side By);
// docs:end

// docs:struct-union
// Outcome is a union too, written out by hand. `union Outcome(InPlay, ...)`
// would keep its case in a field of type object, so each of the 50 outcomes
// a second would be boxed, and zero mode never frees a box. This one keeps
// the case in its own fields. The compiler takes it for a union all the
// same: `return new HitWall();` goes through a constructor, and a switch
// through TryGetValue, so nothing is allocated.
[Union]
readonly struct Outcome : IUnion
{
    enum Case : byte { None, InPlay, HitWall, HitPaddle, Scored }

    readonly Case _case;
    readonly Side _side;    // HitPaddle's Side, Scored's By

    public Outcome(InPlay value) => _case = Case.InPlay;
    public Outcome(HitWall value) => _case = Case.HitWall;
    public Outcome(HitPaddle value) =>
        (_case, _side) = (Case.HitPaddle, value.Side);
    public Outcome(Scored value) => (_case, _side) = (Case.Scored, value.By);

    public bool HasValue => _case != Case.None;

    public bool TryGetValue(out InPlay value)
    {
        value = new();
        return _case == Case.InPlay;
    }

    public bool TryGetValue(out HitWall value)
    {
        value = new();
        return _case == Case.HitWall;
    }

    public bool TryGetValue(out HitPaddle value)
    {
        value = new(_side);
        return _case == Case.HitPaddle;
    }

    public bool TryGetValue(out Scored value)
    {
        value = new(_side);
        return _case == Case.Scored;
    }


    // What IUnion asks for. It boxes; nothing in the game calls it.
    public object Value => _case switch
    {
        Case.InPlay => new InPlay(),
        Case.HitWall => new HitWall(),
        Case.HitPaddle => new HitPaddle(_side),
        Case.Scored => new Scored(_side),
        _ => null,
    };
}
// docs:end

// One match: everything that changes as it is played.
struct Match
{
    public Ball Ball;
    public int LeftTop, RightTop;   // the paddles' top edges
    public Score Score;
    public int Hits, Frames, Strays;
    int _rally;                     // hits since the last serve
    uint _seed;

    public Match(uint seed)
    {
        _seed = seed;
        LeftTop = RightTop = Court.Center - Court.PaddleHeight / 2;
        Serve(towards: Next(2) == 0 ? Side.Right : Side.Left);
    }

    // The same numbers every match, so a test match always goes the same way.
    int Next(int limit)
    {
        _seed = _seed * 1103515245 + 12345;
        return (int)((_seed >> 16) % (uint)limit);
    }

    // From the middle, slowly, at an angle that varies.
    public void Serve(Side towards)
    {
        int dy = 1 + Next(2);
        Ball = new Ball(Court.Middle - Court.BallSize / 2, Court.Center - Court.BallSize / 2,
            Dx: towards == Side.Right ? 2 : -2, Dy: Next(2) == 0 ? -dy : dy);
        _rally = 0;
    }

    public readonly bool OnField =>
        Ball.Y is >= Court.Top and <= Court.Bottom - Court.BallSize
        && LeftTop == Court.Clamp(LeftTop) && RightTop == Court.Clamp(RightTop);

    // docs:frame
    public Outcome Step(Controller left, Controller right)
    {
        LeftTop = Move(LeftTop, left, Side.Left);
        RightTop = Move(RightTop, right, Side.Right);
        Ball = Ball.Moved();

        Outcome outcome = new InPlay();
        if (Ball.Y is < Court.Top or > Court.Bottom - Court.BallSize)
        {
            Ball = Ball with { Y = Math.Clamp(Ball.Y, Court.Top, Court.Bottom - Court.BallSize), Dy = -Ball.Dy };
            outcome = new HitWall();
        }
        if (Ball is { Dx: < 0, X: <= Court.LeftFace and > Court.LeftFace - 8 } && Ball.Meets(LeftTop))
            outcome = Bounce(LeftTop, Court.LeftFace, Side.Left);
        else if (Ball is { Dx: > 0, X: >= Court.RightFace - Court.BallSize and < Court.RightFace - Court.BallSize + 8 }
            && Ball.Meets(RightTop))
            outcome = Bounce(RightTop, Court.RightFace - Court.BallSize, Side.Right);

        return Ball.X switch
        {
            < Court.Left => new Scored(By: Side.Right),
            > Court.Right - Court.BallSize => new Scored(By: Side.Left),
            _ => outcome,
        };
    }

    int Move(int top, Controller who, Side side) => Court.Clamp(who switch
    {
        Human human => top + human.Speed * Joystick.Direction(),
        Computer computer => top + Math.Clamp(Target(side) - (top + Court.PaddleHeight / 2),
            -computer.Speed, computer.Speed),
    });

    // The computer follows the ball once it is on its side and coming its
    // way; otherwise it goes back to the middle. It moves slower than a
    // steep ball, so it can be beaten.
    readonly int Target(Side side)
    {
        bool coming = side == Side.Left
            ? Ball is { Dx: < 0, X: < Court.Middle }
            : Ball is { Dx: > 0, X: > Court.Middle };
        return coming ? Ball.Middle : Court.Center;
    }

    // Off a paddle: the further from its middle the ball hits, the steeper it
    // leaves, and every fourth hit of a rally makes it faster.
    Outcome Bounce(int paddleTop, int x, Side side)
    {
        int dy = (Ball.Middle - (paddleTop + Court.PaddleHeight / 2)) / 6;
        if (dy == 0)
            dy = Next(2) == 0 ? -1 : 1;
        int dx = -Ball.Dx;
        _rally++;
        Hits++;
        if (_rally % 4 == 0 && dx is > -4 and < 4)
            dx += dx > 0 ? 1 : -1;
        Ball = Ball with { X = x, Dx = dx, Dy = dy };
        return new HitPaddle(side);
    }
    // docs:end
}

// W and S, or the joystick in port 2: -1 for up, 1 for down, 0 for neither.
static class Joystick
{
    public static int Direction()
    {
        int stick = C64.Joystick;
        if (C64.KeyHeld('w') || (stick & C64.Up) != 0)
            return -1;
        if (C64.KeyHeld('s') || (stick & C64.Down) != 0)
            return 1;
        return 0;
    }
}

// docs:sound
// One of the SID's three voices. Each has 7 registers from $D400: the
// frequency, the pulse width, the control register (the waveform, and the
// gate that starts a note), and the envelope.
readonly record struct Voice(int Number)
{
    public const int Triangle = 0x10, Sawtooth = 0x20, Pulse = 0x40, Noise = 0x80;

    int Register(int offset) => 0xD400 + Number * 7 + offset;

    // How loud a note is over time: it starts at full volume at once and
    // fades out over `decay` (0-15; 8 is about 300 ms). Sustain and release
    // are 0, so a note dies away by itself.
    public void Envelope(int decay)
    {
        C64.Poke(Register(5), decay);
        C64.Poke(Register(6), 0);
    }

    // Plays a note: frequency is the SID's own number (see Music.Frequency).
    // Turning the gate off and on again starts the note from the top.
    public void Strike(int frequency, int waveform)
    {
        C64.Poke(Register(0), frequency & 0xFF);
        C64.Poke(Register(1), frequency >> 8);
        C64.Poke(Register(2), 0);
        C64.Poke(Register(3), 8);                   // pulse width: half
        C64.Poke(Register(4), waveform);            // gate off
        C64.Poke(Register(4), waveform | 1);        // gate on
    }

    public void Silence() => C64.Poke(Register(4), 0);
}

enum Effect { Wall, Paddle, Point }

// The sound effects, on the first voice.
static class Sound
{
    static readonly Voice Effects = new(0);

    public static void On() => C64.Poke(0xD418, 15);   // full volume
    public static void Off() => C64.Poke(0xD418, 0);

    public static void Play(Effect effect)
    {
        var (frequency, waveform, decay) = effect switch
        {
            Effect.Wall => (12000, Voice.Triangle, 7),
            Effect.Paddle => (20000, Voice.Sawtooth, 8),
            _ => (3000, Voice.Noise, 10),         // a point: a burst of noise
        };
        Effects.Envelope(decay);
        Effects.Strike(frequency, waveform);
    }
}

// A tune of my own that loops: a melody on the second voice, a bass line on
// the third. Tick plays the next step every 7 frames, so it costs nothing
// in between.
static class Music
{
    const int FramesPerStep = 7;

    // MIDI note numbers (60 is middle C), one per step; 0 lets the note
    // before it ring on. Four bars of eight steps: A minor, F, C, G.
    static ReadOnlySpan<byte> Melody =>
    [
        76, 0, 72, 76, 81, 0, 79, 76,
        77, 0, 72, 77, 81, 0, 79, 77,
        79, 0, 76, 72, 74, 76, 79, 0,
        74, 0, 71, 74, 79, 77, 74, 71,
    ];

    static ReadOnlySpan<byte> Bass =>
    [
        45, 0, 57, 0, 45, 0, 57, 0,
        41, 0, 53, 0, 41, 0, 53, 0,
        48, 0, 60, 0, 48, 0, 60, 0,
        43, 0, 55, 0, 43, 0, 55, 0,
    ];

    static readonly Voice Tune = new(1), Low = new(2);
    static int s_frame, s_step;

    public static bool Playing { get; private set; }

    public static void Start()
    {
        Tune.Envelope(decay: 8);
        Low.Envelope(decay: 9);
        s_frame = s_step = 0;
        Playing = true;
    }

    public static void Stop()
    {
        Tune.Silence();
        Low.Silence();
        Playing = false;
    }

    public static void Toggle()
    {
        if (Playing)
            Stop();
        else
            Start();
    }

    public static void Tick()
    {
        if (!Playing || s_frame++ % FramesPerStep != 0)
            return;
        if (Melody[s_step] is not 0 and var note)
            Tune.Strike(Frequency(note), Voice.Pulse);
        if (Bass[s_step] is not 0 and var bass)
            Low.Strike(Frequency(bass), Voice.Sawtooth);
        s_step = (s_step + 1) % Melody.Length;
    }

    // The SID's number for a note: the frequency in Hz times 17.03 on a
    // European (PAL) C64. The table is one high octave, MIDI 84-95; each
    // octave below is half the number.
    static int Frequency(int note) => (note % 12) switch
    {
        0 => 17820, 1 => 18880, 2 => 20003, 3 => 21192,
        4 => 22452, 5 => 23787, 6 => 25202, 7 => 26700,
        8 => 28288, 9 => 29970, 10 => 31752, _ => 33640,
    } >> (7 - note / 12);
}
// docs:end

// The paddles and the ball are sprites; the net and the score are text.
static class Screen
{
    const int LeftPaddle = 0, RightPaddle = 1, Ball = 2;   // sprite numbers

    public static void DrawCourt(Score score)
    {
        C64.Clear(C64.Black);
        C64.Border(C64.DarkGray);
        for (int row = 0; row < C64.Rows; row += 2)    // the net: screen code 93, a vertical line
        {
            C64.Poke(0x0400 + row * C64.Columns + 20, 93);
            C64.Poke(0xD800 + row * C64.Columns + 20, C64.Gray);
        }
        ShowScore(score);

        // A paddle is a bar 4 pixels wide, its 21 rows doubled; the ball is 4 by 4.
        Span<byte> shape = stackalloc byte[63];
        for (int row = 0; row < 21; row++)
            shape[row * 3] = 0xF0;
        C64.Sprite(LeftPaddle, 0, shape, C64.White);
        C64.Sprite(RightPaddle, 0, shape, C64.White);
        C64.Poke(0xD017, C64.Peek(0xD017) | 3);    // sprites 0 and 1 twice as tall
        shape[(Court.BallSize * 3)..].Clear();
        C64.Sprite(Ball, 1, shape, C64.Yellow);
    }

    public static void ShowScore(Score score)
    {
        C64.Print(16, 1, score.Left, C64.LightBlue);
        C64.Print(23, 1, score.Right, C64.LightRed);
    }

    public static void Place(in Match match)
    {
        C64.MoveSprite(LeftPaddle, Court.LeftFace - 4, match.LeftTop);
        C64.MoveSprite(RightPaddle, Court.RightFace, match.RightTop);
        C64.MoveSprite(Ball, match.Ball.X, match.Ball.Y);
    }

    public static void Hide()
    {
        C64.HideSprite(LeftPaddle);
        C64.HideSprite(RightPaddle);
        C64.HideSprite(Ball);
    }

    // Back to the colors BASIC starts with.
    public static void Leave()
    {
        Hide();
        C64.Clear(C64.Blue);
        C64.Border(C64.LightBlue);
    }

    // Waits for the next frame, and plays the music's next step.
    public static void NextFrame()
    {
        C64.WaitFrame();
        Music.Tick();
    }

    public static void Wait(int frames)
    {
        for (int i = 0; i < frames; i++)
            NextFrame();
    }
}

enum Mode { Play, Demo, Test, Quit }

public static class Program
{
    // docs:play
    // Plays the match until someone wins (returns true) or a key stops it:
    // Q always, any key in a demo (returns false).
    static bool Play(ref Match match, Controller left, Controller right, bool demo,
        int maxFrames = int.MaxValue)
    {
        Screen.DrawCourt(match.Score);
        int pause = 50;    // a second before each serve
        for (; match.Frames < maxFrames; match.Frames++)
        {
            Screen.NextFrame();
            Screen.Place(match);
            int key = C64.ReadKey();
            if (key == 'm')
                Music.Toggle();
            else if (key == 'q' || (demo && key != 0))
                return false;
            if (pause > 0)
            {
                pause--;
                continue;
            }

            Outcome outcome = match.Step(left, right);
            if (!match.OnField)
                match.Strays++;
            switch (outcome)
            {
                case HitWall:
                    Sound.Play(Effect.Wall);
                    break;
                case HitPaddle:
                    Sound.Play(Effect.Paddle);
                    break;
                case Scored(var side):
                    match.Score = match.Score.For(side);
                    Screen.ShowScore(match.Score);
                    Sound.Play(Effect.Point);
                    if (match.Score.Winner is not null)
                        return true;
                    match.Serve(towards: side.Opposite);
                    pause = 50;
                    break;
            }
        }
        return true;
    }
    // docs:end

    static Mode Title()
    {
        Screen.Hide();
        C64.Clear(C64.Black);
        C64.Border(C64.DarkGray);
        string name = "P A D D L E S";
        for (int i = 0; i < name.Length; i++)
            C64.Print(13 + i, 4, name[i], C64.Cyan + i % 6);
        C64.Print(6, 8, "You are the left paddle:", C64.LightGray);
        C64.Print(6, 10, "W and S, or a joystick in port 2.", C64.LightGray);
        C64.Print(6, 12, "The first to 7 points wins.", C64.LightGray);
        C64.Print(6, 16, "SPACE  play", C64.White);
        C64.Print(6, 18, "M      music on or off", C64.White);
        C64.Print(6, 20, "Q      back to BASIC", C64.White);
        C64.Print(3, 23, "Or wait, and the C64 plays itself.", C64.Gray);

        for (int frame = 0; frame < 500; frame++)   // ten seconds
        {
            Screen.NextFrame();
            int key = C64.ReadKey();
            if (key == 'm')
                Music.Toggle();
            Mode? chosen = key switch
            {
                ' ' => Mode.Play,
                't' => Mode.Test,
                'q' => Mode.Quit,
                _ => null,
            };
            if (chosen is Mode mode)
                return mode;
        }
        return Mode.Demo;
    }

    static void GameOver(Side winner, bool demo)
    {
        string text = (winner, demo) switch
        {
            (Side.Left, false) => "You win!",
            (Side.Right, false) => "The C64 wins",
            (Side.Left, true) => "Left wins",
            _ => "Right wins",
        };
        C64.Print(20 - text.Length / 2, 12, text, C64.Yellow);
        Screen.Wait(150);
    }

    // One match, computer against computer, then what happened in it: as
    // text, after the screen is cleared.
    static int Test(Controller computer)
    {
        const int MaxFrames = 25000;   // 8 minutes; a match takes a few
        var match = new Match(seed: 1982);
        long before = GC.GetTotalAllocatedBytes();
        Play(ref match, computer, computer, demo: false, MaxFrames);
        long allocated = GC.GetTotalAllocatedBytes() - before;
        Screen.Leave();
        Console.WriteLine($"Test match: {match.Score.Left} to {match.Score.Right}, {match.Hits} hits in {match.Frames} frames");

        int failures = 0;
        void Check(string name, bool ok)
        {
            if (!ok)
            {
                failures++;
                Console.WriteLine($"  failed: {name}");
            }
        }
        Check("someone won", match.Score.Winner is not null);
        Check("it ended in time", match.Frames < MaxFrames);
        Check("the paddles hit the ball", match.Hits > 2 * (match.Score.Left + match.Score.Right));
        Check("everything stayed on the field", match.Strays == 0);
        Check("nothing was allocated while playing", allocated == 0);
        if (failures == 0)
            Console.WriteLine("All 5 checks passed.");
        return failures;
    }

    public static int Main()
    {
        Controller you = new Human(Speed: 3);
        Controller computer = new Computer(Speed: 2);
        Sound.On();
        Music.Start();

        int result = 0;
        for (Mode mode = Title(); mode != Mode.Quit; mode = Title())
        {
            if (mode == Mode.Test)
            {
                result = Test(computer);
                break;
            }
            bool demo = mode == Mode.Demo;
            var match = new Match(seed: 1982);
            if (Play(ref match, demo ? computer : you, computer, demo) && match.Score.Winner is Side winner)
                GameOver(winner, demo);
        }

        Music.Stop();
        Sound.Off();
        if (result == 0)
            Screen.Leave();
        return result;
    }
}
