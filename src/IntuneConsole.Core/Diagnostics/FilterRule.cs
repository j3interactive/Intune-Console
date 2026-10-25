using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Diagnostics;

/// <summary>Evaluates an Intune assignment filter rule against what the console knows about a device,
/// e.g. <c>(device.manufacturer -eq "Dell Inc.") and (device.model -startsWith "Latitude")</c>.
/// Returns true or false when every property in the rule is known, and null when the rule uses a
/// property the console can't see (Intune then decides on the device itself).</summary>
public static class FilterRule
{
    public sealed class Result
    {
        public bool? Matches { get; init; }
        public List<string> UnknownProperties { get; init; } = new();
        public string? ParseError { get; init; }
    }

    public static Result Evaluate(string? rule, DeviceFacts facts)
    {
        if (string.IsNullOrWhiteSpace(rule)) return new Result { Matches = null, ParseError = "Empty rule" };
        try
        {
            var p = new Parser(Tokenize(rule), facts);
            var value = p.ParseOr();
            if (!p.AtEnd) throw new FormatException($"Unexpected '{p.Peek}'");
            return new Result { Matches = value, UnknownProperties = p.Unknown.Distinct().ToList() };
        }
        catch (FormatException ex)
        {
            return new Result { Matches = null, ParseError = ex.Message };
        }
    }

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '(' or ')' or '[' or ']' or ',') { tokens.Add(c.ToString()); i++; continue; }
            if (c is '"' or '\'')
            {
                var end = s.IndexOf(c, i + 1);
                if (end < 0) throw new FormatException("Unclosed quote");
                tokens.Add("\u0001" + s[(i + 1)..end]);   // marker: string literal
                i = end + 1;
                continue;
            }
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('(' or ')' or '[' or ']' or ',' or '"' or '\'')) i++;
            tokens.Add(s[start..i]);
        }
        return tokens;
    }

    private sealed class Parser
    {
        private readonly List<string> _t;
        private readonly DeviceFacts _f;
        private int _i;
        public List<string> Unknown { get; } = new();

        public Parser(List<string> tokens, DeviceFacts facts) { _t = tokens; _f = facts; }

        public bool AtEnd => _i >= _t.Count;
        public string Peek => AtEnd ? "" : _t[_i];
        private string Next() => AtEnd ? throw new FormatException("Rule ended early") : _t[_i++];
        private bool Accept(string s)
        {
            if (!AtEnd && string.Equals(_t[_i], s, StringComparison.OrdinalIgnoreCase)) { _i++; return true; }
            return false;
        }

        public bool? ParseOr()
        {
            var v = ParseAnd();
            while (Accept("or") || Accept("-or")) v = Or(v, ParseAnd());
            return v;
        }

        private bool? ParseAnd()
        {
            var v = ParseUnary();
            while (Accept("and") || Accept("-and")) v = And(v, ParseUnary());
            return v;
        }

        private bool? ParseUnary()
        {
            if (Accept("not") || Accept("-not")) return ParseUnary() is { } b ? !b : null;
            if (Accept("("))
            {
                var v = ParseOr();
                if (!Accept(")")) throw new FormatException("Missing ')'");
                return v;
            }
            return ParseComparison();
        }

        private bool? ParseComparison()
        {
            var prop = Next();
            if (!prop.StartsWith("device.", StringComparison.OrdinalIgnoreCase)) throw new FormatException($"Expected a device property, got '{prop}'");
            var op = Next().ToLowerInvariant();
            List<string> values;
            if (op is "-in" or "-notin")
            {
                if (!Accept("[")) throw new FormatException("Expected '[' after -in");
                values = new();
                while (!Accept("]"))
                {
                    values.Add(Literal(Next()));
                    Accept(",");
                }
            }
            else values = [Literal(Next())];

            var actual = Property(prop[7..]);
            if (actual is null)
            {
                Unknown.Add(prop);
                return null;
            }
            return Compare(prop[7..], actual, op, values);
        }

        private static string Literal(string token) => token.StartsWith('\u0001') ? token[1..] : token;

        private string? Property(string name) => name.ToLowerInvariant() switch
        {
            "devicename" => _f.DeviceName,
            "manufacturer" => _f.Manufacturer,
            "model" => _f.Model,
            "osversion" or "operatingsystemversion" => _f.OsVersion,
            "deviceownership" => _f.Ownership?.ToLowerInvariant() switch
            {
                "company" or "corporate" => "Corporate",
                "personal" => "Personal",
                _ => null,
            },
            "devicecategory" => _f.DeviceCategory,
            "enrollmentprofilename" => _f.EnrollmentProfileName,
            _ => null,
        };

        private static bool Compare(string prop, string actual, string op, List<string> values)
        {
            var v = values.FirstOrDefault() ?? "";
            var ci = StringComparison.OrdinalIgnoreCase;
            switch (op)
            {
                case "-eq": return VersionCompare(prop, actual, v) is { } c0 ? c0 == 0 : actual.Equals(v, ci);
                case "-ne": return VersionCompare(prop, actual, v) is { } c1 ? c1 != 0 : !actual.Equals(v, ci);
                case "-startswith": return actual.StartsWith(v, ci);
                case "-notstartswith": return !actual.StartsWith(v, ci);
                case "-endswith": return actual.EndsWith(v, ci);
                case "-notendswith": return !actual.EndsWith(v, ci);
                case "-contains": return actual.Contains(v, ci);
                case "-notcontains": return !actual.Contains(v, ci);
                case "-in": return values.Any(x => actual.Equals(x, ci));
                case "-notin": return !values.Any(x => actual.Equals(x, ci));
                case "-gt": return (VersionCompare(prop, actual, v) ?? throw new FormatException($"-gt needs a version on {prop}")) > 0;
                case "-ge": return (VersionCompare(prop, actual, v) ?? throw new FormatException($"-ge needs a version on {prop}")) >= 0;
                case "-lt": return (VersionCompare(prop, actual, v) ?? throw new FormatException($"-lt needs a version on {prop}")) < 0;
                case "-le": return (VersionCompare(prop, actual, v) ?? throw new FormatException($"-le needs a version on {prop}")) <= 0;
                default: throw new FormatException($"Operator {op} isn't supported");
            }
        }

        private static int? VersionCompare(string prop, string a, string b)
        {
            if (!prop.Contains("version", StringComparison.OrdinalIgnoreCase)) return null;
            return Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) ? va.CompareTo(vb) : null;
        }

        private static bool? And(bool? a, bool? b) => a == false || b == false ? false : a is null || b is null ? null : true;
        private static bool? Or(bool? a, bool? b) => a == true || b == true ? true : a is null || b is null ? null : false;
    }
}
