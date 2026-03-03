using System;
using System.Text;

namespace ClaudeCodeBridge
{
    internal static class ClaudeCodeHelpers
    {
        internal static string QuoteArg(string arg)
        {
            // Simple quoting for process arguments
            if (arg.Contains("\""))
                arg = arg.Replace("\"", "\\\"");
            return "\"" + arg + "\"";
        }

        // Minimal JSON string extraction - finds "key":"value" pairs.
        // Does not handle nested objects as values, only simple strings and numbers.
        internal static string ExtractJsonString(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;

            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;

            // Skip whitespace
            int valStart = colonIdx + 1;
            while (valStart < json.Length && json[valStart] == ' ') valStart++;
            if (valStart >= json.Length) return null;

            char c = json[valStart];
            if (c == '"')
            {
                // String value
                int strStart = valStart + 1;
                int strEnd = strStart;
                while (strEnd < json.Length)
                {
                    if (json[strEnd] == '\\') { strEnd += 2; continue; }
                    if (json[strEnd] == '"') break;
                    strEnd++;
                }
                return json.Substring(strStart, strEnd - strStart);
            }
            else if (c == 'n') // null
            {
                return null;
            }
            else
            {
                // Number or boolean
                int numEnd = valStart;
                while (numEnd < json.Length && json[numEnd] != ',' && json[numEnd] != '}' && json[numEnd] != ']' && json[numEnd] != ' ')
                    numEnd++;
                return json.Substring(valStart, numEnd - valStart);
            }
        }

        // Extract text content from assistant messages.
        // Looks for "type":"text" blocks within the content array and concatenates their "text" fields.
        internal static string ExtractContentText(string json)
        {
            var sb = new StringBuilder();
            int searchFrom = 0;
            while (true)
            {
                // Find text blocks in content
                int typeTextIdx = json.IndexOf("\"type\":\"text\"", searchFrom, StringComparison.Ordinal);
                if (typeTextIdx < 0)
                    typeTextIdx = json.IndexOf("\"type\": \"text\"", searchFrom, StringComparison.Ordinal);
                if (typeTextIdx < 0) break;

                // Find the "text" field near this type marker
                int textFieldIdx = json.IndexOf("\"text\"", typeTextIdx + 10, StringComparison.Ordinal);
                if (textFieldIdx < 0) break;

                // Make sure we haven't jumped into a different block
                // (check there's no }, { between our type marker and text field indicating a new object)
                string between = json.Substring(typeTextIdx + 10, textFieldIdx - typeTextIdx - 10);
                if (between.Contains("},{"))
                {
                    searchFrom = typeTextIdx + 10;
                    continue;
                }

                string val = ExtractJsonString(json.Substring(typeTextIdx), "text");
                if (val != null)
                {
                    // Unescape common JSON escapes
                    val = val.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
                    sb.Append(val);
                }
                searchFrom = textFieldIdx + 6;
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        // Extract tool name from tool_use blocks
        internal static string ExtractToolUse(string json)
        {
            int idx = json.IndexOf("\"type\":\"tool_use\"", StringComparison.Ordinal);
            if (idx < 0)
                idx = json.IndexOf("\"type\": \"tool_use\"", StringComparison.Ordinal);
            if (idx < 0) return null;

            return ExtractJsonString(json.Substring(idx), "name");
        }
    }
}
