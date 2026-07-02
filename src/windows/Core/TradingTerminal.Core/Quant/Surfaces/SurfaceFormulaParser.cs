namespace TradingTerminal.Core.Quant.Surfaces;

/// <summary>
/// Tiny recursive-descent expression parser for the Surface Lab formula bar. Grammar:
/// <code>
///   expr    := term (('+' | '-') term)*
///   term    := unary (('*' | '/') unary)*
///   unary   := '-' unary | power
///   power   := primary ('^' unary)?            // right-assoc; binds tighter than unary minus
///   primary := number | ident | ident '(' expr (',' expr)* ')' | '(' expr ')'
/// </code>
/// Identifiers are metric ids from <see cref="SurfaceMetricRegistry"/> (e.g. <c>sharpe</c>,
/// <c>maxdd</c>), resolved per cell through a variable resolver. Supported functions:
/// <c>Log, Exp, Sqrt, Abs</c> (unary) and <c>Max, Min, Avg, Sum</c> (variadic). Case-insensitive.
/// Parse once, evaluate per cell — the AST is immutable and thread-safe.
/// </summary>
public sealed class SurfaceFormula
{
    private readonly Node _root;

    private SurfaceFormula(Node root, IReadOnlyList<string> variables)
    {
        _root = root;
        Variables = variables;
    }

    /// <summary>Distinct identifiers referenced by the formula (lower-cased).</summary>
    public IReadOnlyList<string> Variables { get; }

    /// <summary>Evaluates against a per-cell variable resolver. Unknown ids were rejected at parse
    /// time, so <paramref name="resolve"/> is only ever called with ids from <see cref="Variables"/>.</summary>
    public double Evaluate(Func<string, double> resolve) => _root.Eval(resolve);

    /// <summary>Parses <paramref name="text"/>; returns null and an error message on failure.
    /// Identifier validity is checked against <see cref="SurfaceMetricRegistry"/>.</summary>
    public static SurfaceFormula? TryParse(string text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "Empty formula."; return null; }
        try
        {
            var p = new Parser(text);
            var root = p.ParseExpr();
            p.ExpectEnd();
            return new SurfaceFormula(root, p.Variables.ToList());
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // ── AST ──────────────────────────────────────────────────────────────────────────────────

    private abstract class Node
    {
        public abstract double Eval(Func<string, double> resolve);
    }

    private sealed class Num(double value) : Node
    {
        public override double Eval(Func<string, double> resolve) => value;
    }

    private sealed class Var(string id) : Node
    {
        public override double Eval(Func<string, double> resolve) => resolve(id);
    }

    private sealed class Bin(char op, Node l, Node r) : Node
    {
        public override double Eval(Func<string, double> resolve)
        {
            var a = l.Eval(resolve);
            var b = r.Eval(resolve);
            return op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => a / b,
                _   => Math.Pow(a, b),
            };
        }
    }

    private sealed class Neg(Node inner) : Node
    {
        public override double Eval(Func<string, double> resolve) => -inner.Eval(resolve);
    }

    private sealed class Call(string name, Node[] args) : Node
    {
        public override double Eval(Func<string, double> resolve)
        {
            switch (name)
            {
                case "log":  return Math.Log(args[0].Eval(resolve));
                case "exp":  return Math.Exp(args[0].Eval(resolve));
                case "sqrt": return Math.Sqrt(args[0].Eval(resolve));
                case "abs":  return Math.Abs(args[0].Eval(resolve));
            }
            // Variadic aggregates.
            var vals = new double[args.Length];
            for (var i = 0; i < args.Length; i++) vals[i] = args[i].Eval(resolve);
            return name switch
            {
                "max" => vals.Max(),
                "min" => vals.Min(),
                "avg" => vals.Average(),
                _     => vals.Sum(), // "sum"
            };
        }
    }

    // ── Parser ───────────────────────────────────────────────────────────────────────────────

    private sealed class Parser(string text)
    {
        private static readonly string[] UnaryFns = ["log", "exp", "sqrt", "abs"];
        private static readonly string[] VariadicFns = ["max", "min", "avg", "sum"];

        private int _pos;
        public HashSet<string> Variables { get; } = new(StringComparer.Ordinal);

        public Node ParseExpr()
        {
            var left = ParseTerm();
            while (true)
            {
                SkipWs();
                if (_pos < text.Length && (text[_pos] == '+' || text[_pos] == '-'))
                {
                    var op = text[_pos++];
                    left = new Bin(op, left, ParseTerm());
                }
                else return left;
            }
        }

        private Node ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipWs();
                if (_pos < text.Length && (text[_pos] == '*' || text[_pos] == '/'))
                {
                    var op = text[_pos++];
                    left = new Bin(op, left, ParseUnary());
                }
                else return left;
            }
        }

        // '^' binds tighter than unary minus (math/Python convention: -2^2 = -4) and is
        // right-associative; its right operand admits a leading unary so 2^-3 parses.
        private Node ParseUnary()
        {
            SkipWs();
            if (_pos < text.Length && text[_pos] == '-')
            {
                _pos++;
                return new Neg(ParseUnary());
            }
            return ParsePower();
        }

        private Node ParsePower()
        {
            var left = ParsePrimary();
            SkipWs();
            if (_pos < text.Length && text[_pos] == '^')
            {
                _pos++;
                return new Bin('^', left, ParseUnary());
            }
            return left;
        }

        private Node ParsePrimary()
        {
            SkipWs();
            if (_pos >= text.Length) throw new FormatException("Unexpected end of formula.");

            var c = text[_pos];
            if (c == '(')
            {
                _pos++;
                var inner = ParseExpr();
                Expect(')');
                return inner;
            }

            if (char.IsDigit(c) || c == '.')
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.')) _pos++;
                if (!double.TryParse(text[start.._pos], System.Globalization.CultureInfo.InvariantCulture, out var value))
                    throw new FormatException($"Bad number '{text[start.._pos]}'.");
                return new Num(value);
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
                var ident = text[start.._pos].ToLowerInvariant();

                SkipWs();
                if (_pos < text.Length && text[_pos] == '(')
                {
                    if (!UnaryFns.Contains(ident) && !VariadicFns.Contains(ident))
                        throw new FormatException($"Unknown function '{ident}'. Supported: Log, Exp, Sqrt, Abs, Max, Min, Avg, Sum.");
                    _pos++;
                    var args = new List<Node> { ParseExpr() };
                    SkipWs();
                    while (_pos < text.Length && text[_pos] == ',')
                    {
                        _pos++;
                        args.Add(ParseExpr());
                        SkipWs();
                    }
                    Expect(')');
                    if (UnaryFns.Contains(ident) && args.Count != 1)
                        throw new FormatException($"{ident}() takes exactly one argument.");
                    return new Call(ident, args.ToArray());
                }

                if (SurfaceMetricRegistry.Resolve(ident) is null)
                    throw new FormatException($"Unknown variable '{ident}'. Use metric ids like sharpe, maxdd, vol, winrate.");
                Variables.Add(ident);
                return new Var(ident);
            }

            throw new FormatException($"Unexpected character '{c}' at position {_pos}.");
        }

        public void ExpectEnd()
        {
            SkipWs();
            if (_pos < text.Length)
                throw new FormatException($"Unexpected trailing input at position {_pos}: '{text[_pos..]}'.");
        }

        private void Expect(char c)
        {
            SkipWs();
            if (_pos >= text.Length || text[_pos] != c)
                throw new FormatException($"Expected '{c}' at position {_pos}.");
            _pos++;
        }

        private void SkipWs()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }
    }
}
