using System.Globalization;
using System.Text.Json;

namespace Quorum.Modules.Agents.Tools.Native;

/// <summary>
/// Evaluates arithmetic exactly, for models that cannot. A small recursive-descent parser over <see cref="decimal"/>: it only
/// knows numbers, <c>+ - * / % ^</c>, parentheses and a fixed set of functions, so there is nothing to escape from.
/// </summary>
internal sealed class CalculatorTool : INativeTool
{
    public const int MaxExpressionLength = 500;

    /// <summary>Nesting of parentheses, calls, unary operators and powers; keeps the recursion far from the stack limit.</summary>
    public const int MaxDepth = 64;

    public string Name => NativeToolCatalog.Calculator;

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("expression", out var expression)
            || expression.ValueKind != JsonValueKind.String)
        {
            throw new ToolFailedException("Send the expression to evaluate as the 'expression' string.");
        }

        var result = new Parser(expression.GetString()!).Evaluate();
        return Task.FromResult(Format(result));
    }

    // Dividing by 1.000…0 drops the trailing zeros decimal arithmetic leaves (2.50 → 2.5).
    private static string Format(decimal value) =>
        (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    private sealed class Parser(string text)
    {
        private int _position;
        private int _depth;

        private char Current => _position < text.Length ? text[_position] : '\0';

        public decimal Evaluate()
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ToolFailedException("The expression is empty.");
            }

            if (text.Length > MaxExpressionLength)
            {
                throw new ToolFailedException($"The expression is too long: keep it under {MaxExpressionLength} characters.");
            }

            try
            {
                var value = ParseSum();
                SkipSpaces();
                if (_position < text.Length)
                {
                    throw new ToolFailedException(Current == ')'
                        ? "Unexpected ')' without a matching '('."
                        : $"Unexpected '{Current}' at position {_position + 1}.");
                }

                return value;
            }
            catch (OverflowException exception)
            {
                throw new ToolFailedException("The result is out of range: it is too large to represent.", exception);
            }
        }

        private static void RequireArguments(string name, List<decimal> arguments, int min, int max)
        {
            if (arguments.Count < min || arguments.Count > max)
            {
                var expected = max == int.MaxValue ? $"at least {min}" : min == max ? $"{min}" : $"{min} or {max}";
                throw new ToolFailedException($"{name} takes {expected} argument(s) but got {arguments.Count}.");
            }
        }

        private static decimal Round(List<decimal> arguments)
        {
            var digits = 0;
            if (arguments.Count == 2)
            {
                if (arguments[1] != decimal.Truncate(arguments[1]) || arguments[1] is < 0 or > 28)
                {
                    throw new ToolFailedException("round takes a number of decimal places between 0 and 28.");
                }

                digits = (int)arguments[1];
            }

            return Math.Round(arguments[0], digits, MidpointRounding.AwayFromZero);
        }

        // Newton's method on decimals keeps all the digits a decimal has; a double would stop at about 15.
        private static decimal SquareRoot(decimal value)
        {
            if (value < 0)
            {
                throw new ToolFailedException("Cannot take the square root of a negative number.");
            }

            if (value == 0)
            {
                return 0;
            }

            var estimate = (decimal)Math.Sqrt((double)value);
            for (var i = 0; i < 6; i++)
            {
                var next = (estimate + (value / estimate)) / 2;
                if (next == estimate)
                {
                    break;
                }

                estimate = next;
            }

            return estimate;
        }

        private static decimal Power(decimal baseValue, decimal exponent)
        {
            if (exponent == decimal.Truncate(exponent) && Math.Abs(exponent) <= 200)
            {
                if (baseValue == 0 && exponent < 0)
                {
                    throw new ToolFailedException("Cannot divide by zero.");
                }

                var result = 1m;
                for (var i = 0; i < Math.Abs((int)exponent); i++)
                {
                    result *= baseValue;
                }

                return exponent < 0 ? 1 / result : result;
            }

            var value = Math.Pow((double)baseValue, (double)exponent);
            if (double.IsNaN(value))
            {
                throw new ToolFailedException("That power has no real result.");
            }

            if (double.IsInfinity(value) || Math.Abs(value) > (double)decimal.MaxValue)
            {
                throw new ToolFailedException("The result is out of range: it is too large to represent.");
            }

            return (decimal)value;
        }

        private static decimal Call(string function, string displayName, List<decimal> arguments)
        {
            switch (function)
            {
                case "sqrt":
                    RequireArguments(displayName, arguments, 1, 1);
                    return SquareRoot(arguments[0]);
                case "pow":
                    RequireArguments(displayName, arguments, 2, 2);
                    return Power(arguments[0], arguments[1]);
                case "abs":
                    RequireArguments(displayName, arguments, 1, 1);
                    return Math.Abs(arguments[0]);
                case "round":
                    RequireArguments(displayName, arguments, 1, 2);
                    return Round(arguments);
                case "min":
                    RequireArguments(displayName, arguments, 1, int.MaxValue);
                    return arguments.Min();
                case "max":
                    RequireArguments(displayName, arguments, 1, int.MaxValue);
                    return arguments.Max();
                default:
                    throw new ToolFailedException($"Unknown function '{displayName}'. Only sqrt, pow, abs, round, min and max exist.");
            }
        }

        private void SkipSpaces()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }

        private bool Accept(char symbol)
        {
            SkipSpaces();
            if (Current != symbol)
            {
                return false;
            }

            _position++;
            return true;
        }

        private void Enter()
        {
            if (++_depth > MaxDepth)
            {
                throw new ToolFailedException($"The expression is nested too deeply: keep it under {MaxDepth} levels.");
            }
        }

        private decimal ParseSum()
        {
            var value = ParseProduct();
            while (true)
            {
                if (Accept('+'))
                {
                    value += ParseProduct();
                }
                else if (Accept('-'))
                {
                    value -= ParseProduct();
                }
                else
                {
                    return value;
                }
            }
        }

        private decimal ParseProduct()
        {
            var value = ParseUnary();
            while (true)
            {
                if (Accept('*'))
                {
                    value *= ParseUnary();
                }
                else if (Accept('/'))
                {
                    var divisor = ParseUnary();
                    value = divisor == 0 ? throw new ToolFailedException("Cannot divide by zero.") : value / divisor;
                }
                else if (Accept('%'))
                {
                    var divisor = ParseUnary();
                    value = divisor == 0 ? throw new ToolFailedException("Cannot take a remainder by zero.") : value % divisor;
                }
                else
                {
                    return value;
                }
            }
        }

        // A sign binds looser than '^': -2^2 is -4.
        private decimal ParseUnary()
        {
            var negative = Accept('-');
            if (!negative && !Accept('+'))
            {
                return ParsePower();
            }

            Enter();
            try
            {
                return negative ? -ParseUnary() : ParseUnary();
            }
            finally
            {
                _depth--;
            }
        }

        // '^' is right-associative: 2^3^2 is 2^(3^2).
        private decimal ParsePower()
        {
            var baseValue = ParsePrimary();
            if (!Accept('^'))
            {
                return baseValue;
            }

            Enter();
            try
            {
                return Power(baseValue, ParseUnary());
            }
            finally
            {
                _depth--;
            }
        }

        private decimal ParsePrimary()
        {
            SkipSpaces();
            var current = Current;

            if (current == '(')
            {
                _position++;
                Enter();
                try
                {
                    var value = ParseSum();
                    if (!Accept(')'))
                    {
                        throw new ToolFailedException("A closing parenthesis ')' is missing.");
                    }

                    return value;
                }
                finally
                {
                    _depth--;
                }
            }

            if (char.IsAsciiDigit(current) || current == '.')
            {
                return ParseNumber();
            }

            if (char.IsAsciiLetter(current))
            {
                return ParseCall();
            }

            throw new ToolFailedException(_position >= text.Length
                ? "The expression ended where a number, a function or '(' was expected."
                : $"Unexpected '{current}' at position {_position + 1}: a number, a function or '(' was expected.");
        }

        private decimal ParseNumber()
        {
            var start = _position;
            while (char.IsAsciiDigit(Current) || Current == '.')
            {
                _position++;
            }

            var token = text[start.._position];
            if (!decimal.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                throw new ToolFailedException($"'{token}' is not a valid number (at most 28 digits).");
            }

            return value;
        }

        private decimal ParseCall()
        {
            var start = _position;
            while (char.IsAsciiLetter(Current))
            {
                _position++;
            }

            var name = text[start.._position];
            if (!Accept('('))
            {
                throw new ToolFailedException($"Unknown name '{name}'. Only numbers and the functions sqrt, pow, abs, round, min and max exist.");
            }

            Enter();
            try
            {
                var arguments = new List<decimal>();
                if (!Accept(')'))
                {
                    do
                    {
                        arguments.Add(ParseSum());
                    }
                    while (Accept(','));

                    if (!Accept(')'))
                    {
                        throw new ToolFailedException($"A closing parenthesis ')' is missing after the arguments of {name}.");
                    }
                }

                return Call(name.ToLowerInvariant(), name, arguments);
            }
            finally
            {
                _depth--;
            }
        }
    }
}
