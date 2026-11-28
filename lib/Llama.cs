// Llama: runs a small Llama-style language model in llama2.c's file formats
// (model and tokenizer), as Karpathy's tinyllamas ship them. The plan behind
// it is docs/llama-plan.md.
//
//   Llama llama = Llama.Create(model, tokenizer, arena);   // byte spans
//   int count = llama.Encode(prompt, ids);                 // UTF-8 bytes
//   float* logits = llama.Forward(token, position);
//   ReadOnlySpan<byte> piece = llama.Decode(previous, next);
//
// Nothing is copied: the weights are read in place from the model's bytes,
// which can be a file in memory or a literal compiled into the program. The
// run state (activations and the key/value cache) comes from the arena, so
// generating a story allocates nothing after Create. The matrix work goes
// through Kernels (SIMD where -m allows it).
//
// The model file: seven int32s (dim, hidden_dim, n_layers, n_heads,
// n_kv_heads, vocab_size, seq_len; a negative vocab_size means the classifier
// has its own weights), then float32 weights in a fixed order (see Create).
// The tokenizer file: an int32 max piece length, then per token a float32
// score, an int32 length and that many bytes.

using System;

/// <summary>A Llama-style model, its tokenizer and its run state.</summary>
public struct Llama
{
    /// <summary>The model's shape, from its header.</summary>
    public int Dim, HiddenDim, Layers, Heads, KvHeads, VocabSize, SeqLen;

    int _headSize, _kvDim, _kvMul;

    // Weights: pointers into the model's bytes.
    float* _embedding, _rmsAttention, _wq, _wk, _wv, _wo, _rmsFfn, _w1, _w2, _w3, _rmsFinal, _classifier;

    // Run state, from the arena.
    float* _x, _xb, _xb2, _q, _hb, _hb2, _att, _logits, _keyCache, _valueCache, _ropeCos, _ropeSin;

    // Tokenizer: each piece as a pointer and length into the tokenizer's bytes.
    byte** _pieces;
    int* _pieceLengths;
    float* _scores;
    int _maxPieceLength;
    int* _table;      // open addressing: piece bytes -> token id, -1 empty
    int _tableMask;
    byte* _rawBytes;  // the 256 single bytes "<0xXX>" pieces stand for
    byte* _merge;     // scratch for joining two pieces

    /// <summary>Whether Create succeeded.</summary>
    public bool IsCreated => _logits != null;

    /// <summary>
    /// Reads the model and tokenizer, which must stay alive and unchanged
    /// while the result is used, and allocates the run state in
    /// <paramref name="arena"/>. Returns a not-created Llama if either file is
    /// malformed or the arena is out of memory.
    /// <paramref name="maxPositions"/>, when above 0 and below the model's
    /// own limit, is how many tokens Forward will be given at most: SeqLen
    /// becomes that, and the memory for keys and values shrinks with it (all
    /// 512 positions of stories260K take 655 KB, more than a Pico 2 has).
    /// </summary>
    public static Llama Create(ReadOnlySpan<byte> model, ReadOnlySpan<byte> tokenizer, Arena arena, int maxPositions = 0)
    {
        Llama l = default;
        if (model.Length < 28)
            return default;
        unsafe
        {
            // SAFETY: the caller keeps model and tokenizer alive and unmoved
            // (a file in memory, or a literal compiled into the program, which
            // never moves), and every read below is checked against their length.
            // (On real .NET a byte[] does move: there, pass a literal, native
            // memory, or an array from GC.AllocateArray(pinned: true).)
            fixed (byte* m = model)
            fixed (byte* t = tokenizer)
            {
                int* header = (int*)m;
                l.Dim = header[0];
                l.HiddenDim = header[1];
                l.Layers = header[2];
                l.Heads = header[3];
                l.KvHeads = header[4];
                l.VocabSize = Math.Abs(header[5]);
                l.SeqLen = header[6];
                bool shared = header[5] > 0;
                if (l.Dim <= 0 || l.Heads <= 0 || l.KvHeads <= 0 || l.Dim % l.Heads != 0 || l.Heads % l.KvHeads != 0)
                    return default;
                l._headSize = l.Dim / l.Heads;
                l._kvDim = l.Dim * l.KvHeads / l.Heads;
                l._kvMul = l.Heads / l.KvHeads;

                // The weights, in the file's order.
                long dim = l.Dim, hidden = l.HiddenDim, layers = l.Layers, kv = l._kvDim, vocab = l.VocabSize;
                float* p = (float*)(m + 28);
                float* Take(ref float* at, long count) { float* start = at; at += count; return start; }
                l._embedding = Take(ref p, vocab * dim);
                l._rmsAttention = Take(ref p, layers * dim);
                l._wq = Take(ref p, layers * dim * dim);
                l._wk = Take(ref p, layers * dim * kv);
                l._wv = Take(ref p, layers * dim * kv);
                l._wo = Take(ref p, layers * dim * dim);
                l._rmsFfn = Take(ref p, layers * dim);
                l._w1 = Take(ref p, layers * hidden * dim);
                l._w2 = Take(ref p, layers * dim * hidden);
                l._w3 = Take(ref p, layers * hidden * dim);
                l._rmsFinal = Take(ref p, dim);
                Take(ref p, (long)l.SeqLen * l._headSize); // two old RoPE tables, unused
                l._classifier = shared ? l._embedding : Take(ref p, vocab * dim);
                if ((byte*)p - m > model.Length)
                    return default;
                if (maxPositions > 0 && maxPositions < l.SeqLen)
                    l.SeqLen = maxPositions;

                // Run state.
                l._x = arena.AllocArray<float>(l.Dim);
                l._xb = arena.AllocArray<float>(l.Dim);
                l._xb2 = arena.AllocArray<float>(l.Dim);
                l._q = arena.AllocArray<float>(l.Dim);
                l._hb = arena.AllocArray<float>(l.HiddenDim);
                l._hb2 = arena.AllocArray<float>(l.HiddenDim);
                l._att = arena.AllocArray<float>(l.Heads * l.SeqLen);
                l._keyCache = arena.AllocArray<float>(l.Layers * l.SeqLen * l._kvDim);
                l._valueCache = arena.AllocArray<float>(l.Layers * l.SeqLen * l._kvDim);
                l._ropeCos = arena.AllocArray<float>(l.Dim / 2);
                l._ropeSin = arena.AllocArray<float>(l.Dim / 2);
                float* logits = arena.AllocArray<float>(l.VocabSize);
                if (logits == null || l._valueCache == null || l._ropeSin == null)
                    return default;

                if (!l.ReadTokenizer(t, tokenizer.Length, arena))
                    return default;
                l._logits = logits;
            }
        }
        return l;
    }

    unsafe bool ReadTokenizer(byte* t, int length, Arena arena)
    {
        unsafe
        {
            // SAFETY: t holds length bytes (Create's caller), and every read is
            // checked against that length first.
            int vocab = VocabSize;
            _pieces = (byte**)arena.Alloc((nuint)(vocab * sizeof(byte*)));
            _pieceLengths = arena.AllocArray<int>(vocab);
            _scores = arena.AllocArray<float>(vocab);
            _rawBytes = arena.AllocArray<byte>(256);
            int size = 1;
            while (size < vocab * 2)
                size <<= 1;
            _table = arena.AllocArray<int>(size);
            _tableMask = size - 1;
            if (_pieces == null || _pieceLengths == null || _scores == null || _rawBytes == null || _table == null || length < 4)
                return false;
            for (int i = 0; i < 256; i++)
                _rawBytes[i] = (byte)i;
            for (int i = 0; i < size; i++)
                _table[i] = -1;

            _maxPieceLength = *(int*)t;
            int at = 4;
            for (int id = 0; id < vocab; id++)
            {
                if (at + 8 > length)
                    return false;
                _scores[id] = *(float*)(t + at);
                int n = *(int*)(t + at + 4);
                at += 8;
                if (n < 0 || at + n > length)
                    return false;
                _pieces[id] = t + at;
                _pieceLengths[id] = n;
                at += n;
                // First id wins for a duplicated piece, as a sorted lookup would.
                if (Find(_pieces[id], n) < 0)
                {
                    uint slot = Hash(_pieces[id], n) & (uint)_tableMask;
                    while (_table[slot] >= 0)
                        slot = (slot + 1) & (uint)_tableMask;
                    _table[slot] = id;
                }
            }
            _merge = arena.AllocArray<byte>(2 * _maxPieceLength + 2);
            return _merge != null;
        }
    }

    static unsafe uint Hash(byte* bytes, int length)
    {
        uint hash = 2166136261;
        unsafe
        {
            // SAFETY: callers pass a piece or a slice of the prompt, length bytes long.
            for (int i = 0; i < length; i++)
                hash = (hash ^ bytes[i]) * 16777619;
        }
        return hash;
    }

    // The id of the piece with exactly these bytes, or -1.
    unsafe int Find(byte* bytes, int length)
    {
        unsafe
        {
            // SAFETY: the table and pieces were set up by ReadTokenizer.
            uint slot = Hash(bytes, length) & (uint)_tableMask;
            while (true)
            {
                int id = _table[slot];
                if (id < 0)
                    return -1;
                if (_pieceLengths[id] == length && SameBytes(_pieces[id], bytes, length))
                    return id;
                slot = (slot + 1) & (uint)_tableMask;
            }
        }
    }

    static unsafe bool SameBytes(byte* a, byte* b, int length)
    {
        unsafe
        {
            // SAFETY: both hold length bytes (Find's callers).
            for (int i = 0; i < length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Turns UTF-8 text into token ids, as llama2.c does: BOS (1), a leading
    /// space if there is text, a piece per character (or its bytes, as
    /// byte + 3, if the vocabulary lacks it), then repeated merging of the
    /// adjacent pair whose joined piece scores highest. Writes the ids to
    /// <paramref name="ids"/>, which needs room for text.Length + 2, and
    /// returns how many there are, or -1 if it's too small.
    /// </summary>
    public int Encode(ReadOnlySpan<byte> text, Span<int> ids)
    {
        if (ids.Length < text.Length + 2)
            return -1;
        int count = 0;
        ids[count++] = 1;
        unsafe
        {
            // SAFETY: text is a live span; the tokenizer was set up by Create.
            fixed (byte* s = text)
            {
                if (text.Length > 0)
                {
                    byte space = (byte)' ';
                    int spaceId = Find(&space, 1);
                    if (spaceId >= 0)
                        ids[count++] = spaceId;
                }
                for (int i = 0; i < text.Length;)
                {
                    byte lead = s[i];
                    int n = lead < 0x80 ? 1 : lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4;
                    if (i + n > text.Length)
                        n = text.Length - i;
                    int id = Find(s + i, n);
                    if (id >= 0)
                    {
                        ids[count++] = id;
                    }
                    else
                    {
                        for (int k = 0; k < n; k++)
                            ids[count++] = s[i + k] + 3;
                    }
                    i += n;
                }
            }

            while (true)
            {
                float bestScore = -1e10f;
                int best = -1, bestId = -1;
                for (int i = 1; i + 1 < count; i++)
                {
                    int a = ids[i], b = ids[i + 1];
                    int la = _pieceLengths[a], lb = _pieceLengths[b];
                    new ReadOnlySpan<byte>(_pieces[a], la).CopyTo(new Span<byte>(_merge, la));
                    new ReadOnlySpan<byte>(_pieces[b], lb).CopyTo(new Span<byte>(_merge + la, lb));
                    int id = Find(_merge, la + lb);
                    if (id >= 0 && _scores[id] > bestScore)
                    {
                        bestScore = _scores[id];
                        best = i;
                        bestId = id;
                    }
                }
                if (best < 0)
                    return count;
                ids[best] = bestId;
                for (int i = best + 1; i + 1 < count; i++)
                    ids[i] = ids[i + 1];
                count--;
            }
        }
    }

    /// <summary>
    /// The text of <paramref name="token"/>, following
    /// <paramref name="previous"/>: no leading space right after BOS, and the
    /// byte itself for a piece like "&lt;0x0A&gt;". Points into the tokenizer.
    /// </summary>
    public ReadOnlySpan<byte> Decode(int previous, int token)
    {
        unsafe
        {
            // SAFETY: token is checked against the vocabulary; pieces point
            // into the tokenizer bytes Create's caller keeps alive.
            if (token < 0 || token >= VocabSize)
                return default;
            byte* piece = _pieces[token];
            int length = _pieceLengths[token];
            if (previous == 1 && length > 0 && piece[0] == ' ')
            {
                piece++;
                length--;
            }
            if (length == 6 && piece[0] == '<' && piece[1] == '0' && piece[2] == 'x' && piece[5] == '>')
            {
                int value = HexDigit(piece[3]) * 16 + HexDigit(piece[4]);
                if (value >= 0)
                    return new ReadOnlySpan<byte>(_rawBytes + value, 1);
            }
            return new ReadOnlySpan<byte>(piece, length);
        }
    }

    static int HexDigit(byte c)
        => c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -100;

    /// <summary>
    /// Runs the model on <paramref name="token"/> at <paramref name="position"/>
    /// (0, 1, 2, ... in order, below SeqLen) and returns the logits over the
    /// vocabulary, valid until the next call.
    /// </summary>
    public unsafe float* Forward(int token, int position)
    {
        unsafe
        {
            // SAFETY: token and position are checked; every buffer was sized
            // for the model by Create, and the weights lie inside its bytes.
            if (token < 0 || token >= VocabSize || position < 0 || position >= SeqLen)
                return null;
            int dim = Dim, hidden = HiddenDim, headSize = _headSize, kvDim = _kvDim;
            new ReadOnlySpan<float>(_embedding + (long)token * dim, dim).CopyTo(new Span<float>(_x, dim));

            // RoPE rotations for this position, shared by every layer.
            for (int i = 0; i < dim; i += 2)
            {
                float frequency = 1.0f / MathF.Pow(10000.0f, (i % headSize) / (float)headSize);
                float angle = position * frequency;
                _ropeCos[i / 2] = MathF.Cos(angle);
                _ropeSin[i / 2] = MathF.Sin(angle);
            }

            float scale = 1.0f / MathF.Sqrt(headSize);
            for (int l = 0; l < Layers; l++)
            {
                Kernels.RmsNorm(_xb, _x, _rmsAttention + l * dim, dim, 1e-5f);

                float* k = _keyCache + ((long)l * SeqLen + position) * kvDim;
                float* v = _valueCache + ((long)l * SeqLen + position) * kvDim;
                Kernels.MatVec(_q, _wq + (long)l * dim * dim, _xb, dim, dim);
                Kernels.MatVec(k, _wk + (long)l * dim * kvDim, _xb, kvDim, dim);
                Kernels.MatVec(v, _wv + (long)l * dim * kvDim, _xb, kvDim, dim);

                for (int i = 0; i < dim; i += 2)
                {
                    float c = _ropeCos[i / 2], s = _ropeSin[i / 2];
                    float q0 = _q[i], q1 = _q[i + 1];
                    _q[i] = q0 * c - q1 * s;
                    _q[i + 1] = q0 * s + q1 * c;
                    if (i < kvDim)
                    {
                        float k0 = k[i], k1 = k[i + 1];
                        k[i] = k0 * c - k1 * s;
                        k[i + 1] = k0 * s + k1 * c;
                    }
                }

                // Attention, per head; a key/value head serves _kvMul query heads.
                for (int h = 0; h < Heads; h++)
                {
                    float* q = _q + h * headSize;
                    float* att = _att + h * SeqLen;
                    int kvOffset = (h / _kvMul) * headSize;
                    float* keys = _keyCache + (long)l * SeqLen * kvDim + kvOffset;
                    float* values = _valueCache + (long)l * SeqLen * kvDim + kvOffset;
                    for (int t = 0; t <= position; t++)
                        att[t] = Kernels.Dot(q, keys + (long)t * kvDim, headSize) * scale;
                    Kernels.Softmax(att, position + 1);
                    float* output = _xb + h * headSize;
                    for (int i = 0; i < headSize; i++)
                        output[i] = 0;
                    for (int t = 0; t <= position; t++)
                    {
                        float a = att[t];
                        float* value = values + (long)t * kvDim;
                        for (int i = 0; i < headSize; i++)
                            output[i] += a * value[i];
                    }
                }

                Kernels.MatVec(_xb2, _wo + (long)l * dim * dim, _xb, dim, dim);
                for (int i = 0; i < dim; i++)
                    _x[i] += _xb2[i];

                Kernels.RmsNorm(_xb, _x, _rmsFfn + l * dim, dim, 1e-5f);
                Kernels.MatVec(_hb, _w1 + (long)l * hidden * dim, _xb, hidden, dim);
                Kernels.MatVec(_hb2, _w3 + (long)l * hidden * dim, _xb, hidden, dim);
                Kernels.Silu(_hb, hidden);
                for (int i = 0; i < hidden; i++)
                    _hb[i] *= _hb2[i];
                Kernels.MatVec(_xb, _w2 + (long)l * dim * hidden, _hb, dim, hidden);
                for (int i = 0; i < dim; i++)
                    _x[i] += _xb[i];
            }

            Kernels.RmsNorm(_x, _x, _rmsFinal, dim, 1e-5f);
            Kernels.MatVec(_logits, _classifier, _x, VocabSize, dim);
            return _logits;
        }
    }
}

/// <summary>Picks the next token from the logits: greedy at temperature 0, otherwise by chance, optionally from the top p.</summary>
public struct LlamaSampler
{
    ulong _state;
    float _temperature, _topP;
    float* _probabilities;
    int* _order;

    /// <summary>A sampler for a vocabulary of <paramref name="vocabSize"/>, with scratch in <paramref name="arena"/>.</summary>
    public static LlamaSampler Create(int vocabSize, float temperature, float topP, ulong seed, Arena arena)
    {
        LlamaSampler s = default;
        s._state = seed == 0 ? 0x9E3779B97F4A7C15 : seed;
        s._temperature = temperature;
        s._topP = topP;
        s._probabilities = arena.AllocArray<float>(vocabSize);
        s._order = arena.AllocArray<int>(vocabSize);
        return s;
    }

    // xorshift*, as in llama2.c.
    uint NextUInt()
    {
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        return (uint)((_state * 0x2545F4914F6CDD1DUL) >> 32);
    }

    float NextFloat() => (NextUInt() >> 8) / 16777216.0f;

    /// <summary>The next token. May change the logits (they're divided by the temperature).</summary>
    public int Sample(float* logits, int n)
    {
        unsafe
        {
            // SAFETY: logits holds n floats (Llama.Forward's result), and the
            // scratch arrays were sized for the vocabulary.
            if (_temperature == 0)
            {
                int best = 0;
                for (int i = 1; i < n; i++)
                {
                    if (logits[i] > logits[best])
                        best = i;
                }
                return best;
            }
            for (int i = 0; i < n; i++)
                _probabilities[i] = logits[i] / _temperature;
            Kernels.Softmax(_probabilities, n);
            float coin = NextFloat();

            if (_topP <= 0 || _topP >= 1)
            {
                float sum = 0;
                for (int i = 0; i < n; i++)
                {
                    sum += _probabilities[i];
                    if (coin < sum)
                        return i;
                }
                return n - 1;
            }

            // Top p: the most likely tokens whose probabilities add up to p,
            // sorted, ignoring those too unlikely to ever make the cut.
            float cutoff = (1.0f - _topP) / (n - 1);
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                if (_probabilities[i] >= cutoff)
                    _order[count++] = i;
            }
            for (int i = 1; i < count; i++)
            {
                int id = _order[i];
                int j = i - 1;
                while (j >= 0 && _probabilities[_order[j]] < _probabilities[id])
                {
                    _order[j + 1] = _order[j];
                    j--;
                }
                _order[j + 1] = id;
            }
            float total = 0;
            int last = count - 1;
            for (int i = 0; i < count; i++)
            {
                total += _probabilities[_order[i]];
                if (total > _topP)
                {
                    last = i;
                    break;
                }
            }
            float r = coin * total;
            float running = 0;
            for (int i = 0; i <= last; i++)
            {
                running += _probabilities[_order[i]];
                if (r < running)
                    return _order[i];
            }
            return _order[last];
        }
    }
}
