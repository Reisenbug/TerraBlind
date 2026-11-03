using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class ScriptLibrary
{
    static readonly object gate = new();
    readonly string directory;
    public ScriptLibrary(string directory) => this.directory = directory;

    string FilePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 80
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-'))
            throw new ArgumentException("program名称只允许1..80个英文字母、数字、下划线或短横线。");
        return Path.Combine(directory, name + ".json");
    }

    public JsonObject Get(string name)
    {
        lock (gate)
        {
            string path = FilePath(name);
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)).AsObject()
                : new JsonObject { ["name"] = name, ["revision"] = 0, ["found"] = false };
        }
    }

    public JsonArray List()
    {
        lock (gate)
        {
            if (!Directory.Exists(directory)) return new JsonArray();
            return new JsonArray(Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path)
                .Select(path => { var entry = Get(Path.GetFileNameWithoutExtension(path)); entry.Remove("code"); return (JsonNode)entry; }).ToArray());
        }
    }

    public JsonObject Save(string name, string code, int expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 16000)
            throw new ArgumentException("code必须是1..16000字符的JavaScript函数体。");
        lock (gate)
        {
            var previous = Get(name);
            int revision = previous["revision"].GetValue<int>();
            if (revision != expectedRevision)
                return new JsonObject { ["error"] = "program_revision_conflict", ["name"] = name,
                    ["expected_revision"] = expectedRevision, ["revision"] = revision };
            try
            {
                string wrapped = "(function() {\n" + code + "\n});";
                Jint.Engine.PrepareScript(wrapped);
                var script = new Acornima.Parser().ParseScript(wrapped);
                var function = (Acornima.Ast.FunctionExpression)((Acornima.Ast.ExpressionStatement)script.Body[0]).Expression;
                if (function.Body.Body.Any(node => node is Acornima.Ast.FunctionDeclaration)
                    && function.Body.Body.All(node => node is Acornima.Ast.FunctionDeclaration or Acornima.Ast.EmptyStatement))
                    return new JsonObject { ["error"] = "program_missing_invocation", ["name"] = name, ["revision"] = revision,
                        ["detail"] = "源码仅声明函数，没有执行它们。宿主执行的是函数体，不会自动调用main。",
                        ["hint"] = "在末尾显式调用入口，例如return main();，或直接编写执行语句。原版本未修改。" };
            }
            catch (Jint.ScriptPreparationException failure) when (failure.InnerException is Acornima.ParseErrorException)
            {
                var error = (Acornima.ParseErrorException)failure.InnerException;
                return new JsonObject { ["error"] = "program_invalid_syntax", ["name"] = name, ["revision"] = revision,
                    ["line"] = Math.Max(1, error.LineNumber - 1), ["column"] = error.Column,
                    ["detail"] = error.Message, ["hint"] = "使用同步JavaScript函数体和tool()；不支持顶层await。原版本未修改。" };
            }
            var saved = new JsonObject { ["name"] = name, ["found"] = true, ["revision"] = revision + 1, ["code"] = code };
            Directory.CreateDirectory(directory);
            string path = FilePath(name), temporary = path + ".tmp";
            File.WriteAllText(temporary, saved.ToJsonString());
            File.Move(temporary, path, true);
            return saved;
        }
    }

    public JsonObject Patch(string name, string oldText, string newText, int expectedRevision)
    {
        if (string.IsNullOrEmpty(oldText) || newText == null) throw new ArgumentException("patch需要非空old_text和new_text。");
        lock (gate)
        {
            var previous = Get(name);
            int revision = previous["revision"].GetValue<int>();
            if (revision != expectedRevision)
                return new JsonObject { ["error"] = "program_revision_conflict", ["name"] = name, ["revision"] = revision };
            string code = previous["code"]?.GetValue<string>();
            int start = code?.IndexOf(oldText, StringComparison.Ordinal) ?? -1;
            if (start < 0 || code.IndexOf(oldText, start + 1, StringComparison.Ordinal) >= 0)
                return new JsonObject { ["error"] = "program_patch_not_unique", ["name"] = name, ["revision"] = revision };
            return Save(name, code[..start] + newText + code[(start + oldText.Length)..], expectedRevision);
        }
    }
}
