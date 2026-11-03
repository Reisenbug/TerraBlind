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
            var saved = new JsonObject { ["name"] = name, ["found"] = true, ["revision"] = revision + 1, ["code"] = code };
            Directory.CreateDirectory(directory);
            string path = FilePath(name), temporary = path + ".tmp";
            File.WriteAllText(temporary, saved.ToJsonString());
            File.Move(temporary, path, true);
            return saved;
        }
    }
}
