using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Drivers;

/// <summary>An expression that will not read, with where in it the trouble is.</summary>
/// <param name="message">What is wrong, in words.</param>
/// <param name="position">The character it starts at, from 0.</param>
public sealed class DriverSyntaxException(string message, int position) : FormatException($"{message} (at character {position + 1})")
{
    /// <summary>The character the trouble starts at, from 0.</summary>
    public int Position { get; } = position;

    /// <summary>What is wrong, without the position.</summary>
    public string Reason { get; } = message;
}

/// <summary>A driver's value: one to four numbers, a single number standing for all of them.</summary>
/// <param name="Value">The numbers.</param>
/// <param name="Count">How many of them mean something, 1 to 4.</param>
public readonly record struct DriverValue(Vector4 Value, int Count)
{
    /// <summary>One number.</summary>
    public static DriverValue Of(double value) => new(new Vector4((float)value), 1);

    /// <summary>The first number.</summary>
    public float X => Value.X;

    /// <summary>One component, the number itself for a single number.</summary>
    public float this[int index] => Count == 1 ? Value.X : index switch
    {
        0 => Value.X,
        1 => Value.Y,
        2 => Value.Z,
        _ => Value.W,
    };
}

/// <summary>What an expression is evaluated for: the time on its owner and the value it drives.</summary>
/// <param name="Local">Time from the owner's start.</param>
/// <param name="Base">The keyframed or fixed value underneath, which <c>value</c> reads.</param>
/// <param name="Environment">The project, sequence and sound, or null for time alone.</param>
/// <param name="Origin">Where the owner starts on the sequence.</param>
public readonly record struct DriverFrame(Flicks Local, DriverValue Base, IDriverEnvironment? Environment, Flicks Origin)
{
    /// <summary>The time on the sequence.</summary>
    public Flicks Sequence => Origin + Local;

    /// <summary>The frame rate, 30 when nothing says.</summary>
    public Rational Rate => Environment?.FrameRate ?? Rational.Fps30;
}

/// <summary>
/// A parameter's driver: a small arithmetic language over time, noise, sound, markers and other
/// parameters, compiled once and evaluated each frame without allocating.
/// </summary>
/// <remarks>
/// <para>
/// Numbers, <c>+ - * / % ^</c>, brackets, and vectors <c>[a, b]</c> with <c>.x .y .z .w</c>. A
/// single number stands for all components, so <c>1 + audio("Music", low) * 0.15</c> scales
/// both ways at once. Names: <c>time</c> (seconds from the owner's start), <c>frame</c>,
/// <c>value</c> (the keyframed or fixed value underneath) and <c>pi</c>.
/// </para>
/// <para>
/// Functions: <c>sin cos tan abs sign floor ceil round sqrt exp log min max pow clamp lerp
/// smoothstep</c>; <c>noise(x, seed)</c>; <c>wiggle(freq, amount, seed)</c>, smooth random
/// movement, four independent channels; <c>loop(period)</c> and <c>pingpong(period)</c>, time
/// wrapped; <c>audio("track", band, attack, release)</c> the track's sound from 0 to its loudest,
/// band <c>level</c>, <c>low</c>, <c>mid</c> or <c>high</c>; <c>param("id", "name")</c> another
/// parameter at the same moment; <c>marker("name")</c> seconds since the last marker of that name.
/// </para>
/// </remarks>
public sealed class DriverExpression
{
    private static readonly ConcurrentDictionary<string, DriverExpression> Compiled = new(StringComparer.Ordinal);

    private readonly Node _root;

    private DriverExpression(string text, Node root)
    {
        Text = text;
        _root = root;
    }

    /// <summary>What was written.</summary>
    public string Text { get; }

    /// <summary>Reads an expression, or throws a <see cref="DriverSyntaxException"/> naming where it went wrong.</summary>
    public static DriverExpression Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Compiled.TryGetValue(text, out DriverExpression? known))
        {
            return known;
        }

        var parser = new Parser(text);
        DriverExpression expression = new(text, parser.Whole());
        if (Compiled.Count > 4096)
        {
            Compiled.Clear();
        }

        Compiled[text] = expression;
        return expression;
    }

    /// <summary>The expression's value for a frame.</summary>
    public DriverValue Evaluate(in DriverFrame frame) => _root.Eval(in frame);

    private abstract class Node
    {
        /// <summary>True when nothing in it changes with time, sound or the value underneath.</summary>
        public virtual bool IsConstant => false;

        public abstract DriverValue Eval(in DriverFrame frame);
    }

    private sealed class Constant(DriverValue value) : Node
    {
        public override bool IsConstant => true;

        public override DriverValue Eval(in DriverFrame frame) => value;
    }

    private sealed class TimeNode : Node
    {
        public override DriverValue Eval(in DriverFrame frame) => DriverValue.Of(frame.Local.ToSeconds());
    }

    private sealed class FrameNode : Node
    {
        public override DriverValue Eval(in DriverFrame frame) => DriverValue.Of(Math.Floor((frame.Local.ToSeconds() * frame.Rate.ToDouble()) + 1e-9));
    }

    private sealed class BaseNode : Node
    {
        public override DriverValue Eval(in DriverFrame frame) => frame.Base;
    }

    private sealed class VectorNode(Node[] parts) : Node
    {
        public override bool IsConstant => parts.All(part => part.IsConstant);

        public override DriverValue Eval(in DriverFrame frame)
        {
            Vector4 value = default;
            for (int index = 0; index < parts.Length; index++)
            {
                float part = parts[index].Eval(in frame).X;
                value = index switch
                {
                    0 => value with { X = part },
                    1 => value with { Y = part },
                    2 => value with { Z = part },
                    _ => value with { W = part },
                };
            }

            return new DriverValue(value, parts.Length);
        }
    }

    private sealed class ComponentNode(Node of, int index) : Node
    {
        public override bool IsConstant => of.IsConstant;

        public override DriverValue Eval(in DriverFrame frame) => DriverValue.Of(of.Eval(in frame)[index]);
    }

    private sealed class NegateNode(Node of) : Node
    {
        public override bool IsConstant => of.IsConstant;

        public override DriverValue Eval(in DriverFrame frame)
        {
            DriverValue value = of.Eval(in frame);
            return value with { Value = -value.Value };
        }
    }

    private sealed class BinaryNode(Node left, Node right, char op) : Node
    {
        public override bool IsConstant => left.IsConstant && right.IsConstant;

        public override DriverValue Eval(in DriverFrame frame) => Apply(left.Eval(in frame), right.Eval(in frame), op);

        public static DriverValue Apply(DriverValue a, DriverValue b, char op)
        {
            int count = Math.Max(a.Count, b.Count);
            Vector4 x = Spread(a);
            Vector4 y = Spread(b);
            Vector4 result = op switch
            {
                '+' => x + y,
                '-' => x - y,
                '*' => x * y,
                '/' => new Vector4(Divide(x.X, y.X), Divide(x.Y, y.Y), Divide(x.Z, y.Z), Divide(x.W, y.W)),
                '%' => new Vector4(Modulo(x.X, y.X), Modulo(x.Y, y.Y), Modulo(x.Z, y.Z), Modulo(x.W, y.W)),
                _ => new Vector4(MathF.Pow(x.X, y.X), MathF.Pow(x.Y, y.Y), MathF.Pow(x.Z, y.Z), MathF.Pow(x.W, y.W)),
            };
            return new DriverValue(Finite(result), count);
        }

        private static float Divide(float a, float b) => b == 0 ? 0 : a / b;

        private static float Modulo(float a, float b) => b == 0 ? 0 : a - (b * MathF.Floor(a / b));
    }

    private sealed class FunctionNode(Func<float, float, float, float> function, Node[] arguments) : Node
    {
        public override bool IsConstant => arguments.All(argument => argument.IsConstant);

        public override DriverValue Eval(in DriverFrame frame)
        {
            DriverValue a = arguments[0].Eval(in frame);
            DriverValue b = arguments.Length > 1 ? arguments[1].Eval(in frame) : default;
            DriverValue c = arguments.Length > 2 ? arguments[2].Eval(in frame) : default;
            int count = Math.Max(a.Count, Math.Max(b.Count, c.Count));
            var result = new Vector4(
                function(a[0], b[0], c[0]),
                function(a[1], b[1], c[1]),
                function(a[2], b[2], c[2]),
                function(a[3], b[3], c[3]));
            return new DriverValue(Finite(result), Math.Max(count, 1));
        }
    }

    private sealed class WiggleNode(Node frequency, Node amount, Node? seed) : Node
    {
        public override DriverValue Eval(in DriverFrame frame)
        {
            double at = frame.Local.ToSeconds() * frequency.Eval(in frame).X;
            float size = amount.Eval(in frame).X;
            uint mixed = (uint)(int)(seed?.Eval(in frame).X ?? 0);
            return new DriverValue(
                new Vector4(
                    (float)DriverNoise.Noise(at, mixed, 0) * size,
                    (float)DriverNoise.Noise(at, mixed, 1) * size,
                    (float)DriverNoise.Noise(at, mixed, 2) * size,
                    (float)DriverNoise.Noise(at, mixed, 3) * size),
                4);
        }
    }

    private sealed class NoiseNode(Node x, Node? seed) : Node
    {
        public override DriverValue Eval(in DriverFrame frame) =>
            DriverValue.Of(DriverNoise.Noise(x.Eval(in frame).X, (uint)(int)(seed?.Eval(in frame).X ?? 0), 0));
    }

    private sealed class LoopNode(Node period, bool pingPong) : Node
    {
        public override DriverValue Eval(in DriverFrame frame)
        {
            double length = period.Eval(in frame).X;
            double time = frame.Local.ToSeconds();
            if (length <= 0)
            {
                return DriverValue.Of(time);
            }

            double wrapped = time - (length * Math.Floor(time / length));
            if (pingPong && ((long)Math.Floor(time / length) & 1) == 1)
            {
                wrapped = length - wrapped;
            }

            return DriverValue.Of(wrapped);
        }
    }

    private sealed class AudioNode(string track, AudioBand band, Node? attack, Node? release) : Node
    {
        public override DriverValue Eval(in DriverFrame frame) => frame.Environment is { } environment
            ? DriverValue.Of(environment.Audio(track, band, frame.Sequence.ToSeconds(), attack?.Eval(in frame).X ?? 0.01, release?.Eval(in frame).X ?? 0.15))
            : DriverValue.Of(0);
    }

    private sealed class ParamNode(string owner, string name) : Node
    {
        public override DriverValue Eval(in DriverFrame frame) => frame.Environment is { } environment
            ? environment.Param(owner, name, frame.Sequence)
            : DriverValue.Of(0);
    }

    private sealed class MarkerNode(string name) : Node
    {
        public override DriverValue Eval(in DriverFrame frame) => frame.Environment is { } environment
            ? DriverValue.Of(environment.SinceMarker(name, frame.Sequence))
            : DriverValue.Of(0);
    }

    private static Vector4 Spread(DriverValue value) => value.Count == 1 ? new Vector4(value.Value.X) : value.Value;

    private static Vector4 Finite(Vector4 value) => new(Finite(value.X), Finite(value.Y), Finite(value.Z), Finite(value.W));

    private static float Finite(float value) => float.IsFinite(value) ? value : 0;

    /// <summary>A recursive descent parser, one character of lookahead after skipping spaces.</summary>
    private ref struct Parser(string text)
    {
        private static readonly Dictionary<string, (int Min, int Max, Func<float, float, float, float> Function)> Maths = new(StringComparer.Ordinal)
        {
            ["sin"] = (1, 1, (a, _, _) => MathF.Sin(a)),
            ["cos"] = (1, 1, (a, _, _) => MathF.Cos(a)),
            ["tan"] = (1, 1, (a, _, _) => MathF.Tan(a)),
            ["abs"] = (1, 1, (a, _, _) => MathF.Abs(a)),
            ["sign"] = (1, 1, (a, _, _) => MathF.Sign(a)),
            ["floor"] = (1, 1, (a, _, _) => MathF.Floor(a)),
            ["ceil"] = (1, 1, (a, _, _) => MathF.Ceiling(a)),
            ["round"] = (1, 1, (a, _, _) => MathF.Round(a, MidpointRounding.AwayFromZero)),
            ["sqrt"] = (1, 1, (a, _, _) => MathF.Sqrt(MathF.Max(a, 0))),
            ["exp"] = (1, 1, (a, _, _) => MathF.Exp(a)),
            ["log"] = (1, 1, (a, _, _) => a > 0 ? MathF.Log(a) : 0),
            ["min"] = (2, 2, (a, b, _) => MathF.Min(a, b)),
            ["max"] = (2, 2, (a, b, _) => MathF.Max(a, b)),
            ["pow"] = (2, 2, (a, b, _) => MathF.Pow(a, b)),
            ["clamp"] = (3, 3, (x, low, high) => MathF.Min(MathF.Max(x, low), high)),
            ["lerp"] = (3, 3, (a, b, t) => a + ((b - a) * t)),
            ["smoothstep"] = (3, 3, (low, high, x) =>
            {
                float t = high == low ? (x < low ? 0 : 1) : Math.Clamp((x - low) / (high - low), 0, 1);
                return t * t * (3 - (2 * t));
            }),
        };

        private readonly string _text = text;
        private int _at;

        public Node Whole()
        {
            Node root = Sum();
            Skip();
            if (_at < _text.Length)
            {
                throw new DriverSyntaxException($"'{_text[_at]}' is not expected here", _at);
            }

            return root;
        }

        private Node Sum()
        {
            Node left = Product();
            while (Peek() is '+' or '-')
            {
                char op = _text[_at++];
                left = Fold(new BinaryNode(left, Product(), op));
            }

            return left;
        }

        private Node Product()
        {
            Node left = Unary();
            while (Peek() is '*' or '/' or '%')
            {
                char op = _text[_at++];
                left = Fold(new BinaryNode(left, Unary(), op));
            }

            return left;
        }

        private Node Unary()
        {
            if (Peek() == '-')
            {
                _at++;
                return Fold(new NegateNode(Unary()));
            }

            if (Peek() == '+')
            {
                _at++;
                return Unary();
            }

            return Power();
        }

        private Node Power()
        {
            Node bottom = Postfix();
            if (Peek() == '^')
            {
                _at++;
                return Fold(new BinaryNode(bottom, Unary(), '^'));
            }

            return bottom;
        }

        private Node Postfix()
        {
            Node node = Primary();
            while (Peek() == '.')
            {
                int start = _at++;
                string name = Word();
                int index = name switch
                {
                    "x" => 0,
                    "y" => 1,
                    "z" => 2,
                    "w" => 3,
                    _ => throw new DriverSyntaxException($"'.{name}' is not a component; use .x, .y, .z or .w", start),
                };
                node = Fold(new ComponentNode(node, index));
            }

            return node;
        }

        private Node Primary()
        {
            char next = Peek();
            int start = _at;
            if (next == '\0')
            {
                throw new DriverSyntaxException("The expression ends where a value is expected", _at);
            }

            if (char.IsAsciiDigit(next) || next == '.')
            {
                return new Constant(DriverValue.Of(Number()));
            }

            if (next == '(')
            {
                _at++;
                Node inner = Sum();
                Expect(')', "Close the bracket opened at character " + (start + 1));
                return inner;
            }

            if (next == '[')
            {
                _at++;
                var parts = new List<Node> { Sum() };
                while (Peek() == ',')
                {
                    _at++;
                    parts.Add(Sum());
                }

                if (parts.Count > 4)
                {
                    throw new DriverSyntaxException("A vector has at most four parts", start);
                }

                Expect(']', "Close the vector opened at character " + (start + 1));
                return Fold(new VectorNode([.. parts]));
            }

            if (next == '"')
            {
                throw new DriverSyntaxException("Text is only for the names in audio, param and marker", start);
            }

            if (!char.IsAsciiLetter(next))
            {
                throw new DriverSyntaxException($"'{next}' is not expected here", start);
            }

            string word = Word();
            if (Peek() == '(')
            {
                return Call(word, start);
            }

            return word switch
            {
                "time" => new TimeNode(),
                "frame" => new FrameNode(),
                "value" => new BaseNode(),
                "pi" => new Constant(DriverValue.Of(Math.PI)),
                "low" or "mid" or "high" or "level" => throw new DriverSyntaxException($"'{word}' is a band, for audio(\"track\", {word})", start),
                _ => throw new DriverSyntaxException($"'{word}' is not a name this knows; try time, frame, value or a function", start),
            };
        }

        private Node Call(string name, int start)
        {
            _at++;
            switch (name)
            {
                case "audio":
                {
                    string track = Text();
                    Expect(',', "audio needs a band after the track: audio(\"Music\", low)");
                    int bandAt = Here();
                    AudioBand band = Word() switch
                    {
                        "level" => AudioBand.Level,
                        "low" => AudioBand.Low,
                        "mid" => AudioBand.Mid,
                        "high" => AudioBand.High,
                        _ => throw new DriverSyntaxException("The band is level, low, mid or high", bandAt),
                    };
                    Node? attack = null;
                    Node? release = null;
                    if (Peek() == ',')
                    {
                        _at++;
                        attack = Sum();
                        if (Peek() == ',')
                        {
                            _at++;
                            release = Sum();
                        }
                    }

                    Expect(')', "Close audio(");
                    return new AudioNode(track, band, attack, release);
                }

                case "param":
                {
                    string owner = Text();
                    Expect(',', "param needs the parameter's name after the owner: param(\"id\", \"opacity\")");
                    string parameter = Text();
                    Expect(')', "Close param(");
                    return new ParamNode(owner, parameter);
                }

                case "marker":
                {
                    string marker = Text();
                    Expect(')', "Close marker(");
                    return new MarkerNode(marker);
                }
            }

            List<Node> arguments = Arguments(start);
            if (name is "wiggle")
            {
                Count(name, arguments, 2, 3, start);
                return new WiggleNode(arguments[0], arguments[1], arguments.Count > 2 ? arguments[2] : null);
            }

            if (name is "noise")
            {
                Count(name, arguments, 1, 2, start);
                return new NoiseNode(arguments[0], arguments.Count > 1 ? arguments[1] : null);
            }

            if (name is "loop" or "pingpong")
            {
                Count(name, arguments, 1, 1, start);
                return new LoopNode(arguments[0], name == "pingpong");
            }

            if (Maths.TryGetValue(name, out (int Min, int Max, Func<float, float, float, float> Function) maths))
            {
                Count(name, arguments, maths.Min, maths.Max, start);
                return Fold(new FunctionNode(maths.Function, [.. arguments]));
            }

            throw new DriverSyntaxException($"There is no function '{name}'", start);
        }

        private List<Node> Arguments(int start)
        {
            var arguments = new List<Node>();
            if (Peek() == ')')
            {
                _at++;
                return arguments;
            }

            arguments.Add(Sum());
            while (Peek() == ',')
            {
                _at++;
                arguments.Add(Sum());
            }

            Expect(')', "Close the call at character " + (start + 1));
            return arguments;
        }

        private static void Count(string name, List<Node> arguments, int min, int max, int start)
        {
            if (arguments.Count < min || arguments.Count > max)
            {
                string wanted = min == max ? $"{min}" : $"{min} to {max}";
                throw new DriverSyntaxException($"{name} takes {wanted} values, not {arguments.Count}", start);
            }
        }

        /// <summary>A node with no time, sound or base in it is worked out once, here.</summary>
        private static Node Fold(Node node) => node.IsConstant && node is not Constant ? new Constant(node.Eval(default)) : node;

        private double Number()
        {
            int start = _at;
            while (_at < _text.Length && (char.IsAsciiDigit(_text[_at]) || _text[_at] == '.'))
            {
                _at++;
            }

            if (_at < _text.Length && _text[_at] is 'e' or 'E')
            {
                _at++;
                if (_at < _text.Length && _text[_at] is '+' or '-')
                {
                    _at++;
                }

                while (_at < _text.Length && char.IsAsciiDigit(_text[_at]))
                {
                    _at++;
                }
            }

            return double.TryParse(_text.AsSpan(start, _at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : throw new DriverSyntaxException($"'{_text[start.._at]}' is not a number", start);
        }

        private string Word()
        {
            Skip();
            int start = _at;
            while (_at < _text.Length && (char.IsAsciiLetterOrDigit(_text[_at]) || _text[_at] == '_'))
            {
                _at++;
            }

            return _at > start ? _text[start.._at] : throw new DriverSyntaxException("A name is expected here", start);
        }

        private string Text()
        {
            if (Peek() != '"')
            {
                throw new DriverSyntaxException("A name in double quotes is expected here", _at);
            }

            int start = _at++;
            int end = _text.IndexOf('"', _at);
            if (end < 0)
            {
                throw new DriverSyntaxException("The quote opened here is never closed", start);
            }

            string value = _text[_at..end];
            _at = end + 1;
            return value;
        }

        private void Expect(char wanted, string help)
        {
            if (Peek() != wanted)
            {
                throw new DriverSyntaxException($"'{wanted}' is expected here. {help}", Math.Min(_at, _text.Length));
            }

            _at++;
        }

        private int Here()
        {
            Skip();
            return _at;
        }

        private char Peek()
        {
            Skip();
            return _at < _text.Length ? _text[_at] : '\0';
        }

        private void Skip()
        {
            while (_at < _text.Length && char.IsWhiteSpace(_text[_at]))
            {
                _at++;
            }
        }
    }
}
