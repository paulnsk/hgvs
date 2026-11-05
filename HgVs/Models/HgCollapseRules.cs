using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HgVs.Models
{
    /// <summary>
    /// Правило сворачивания дерева: какие файлы «нужны». Папки с нужными файлами раскрываются, без них — сворачиваются.
    /// Строка правила: "Имя: выражение". Выражение — маски через пробел:
    /// "маска" — нужен файл, подходящий под любую такую маску; "-маска" — файл не нужен; "unviewed" — только непросмотренные.
    /// Маска без * и ? ищется как подстрока пути, с ними — сравнивается со всем путём. Регистр не важен.
    /// </summary>
    public sealed class HgCollapseRule
    {
        private readonly List<Func<string, bool>> _include = new List<Func<string, bool>>();
        private readonly List<Func<string, bool>> _exclude = new List<Func<string, bool>>();
        private readonly bool _unviewedOnly;

        public string Name { get; }
        public string Expression { get; }

        private HgCollapseRule(string name, string expression)
        {
            Name = name;
            Expression = expression;
            foreach (var token in expression.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(token, "unviewed", StringComparison.OrdinalIgnoreCase))
                    _unviewedOnly = true;
                else if (token.StartsWith("-", StringComparison.Ordinal))
                {
                    if (token.Length > 1) _exclude.Add(CreateMatcher(token.Substring(1)));
                }
                else
                    _include.Add(CreateMatcher(token));
            }
        }

        public bool IsNeeded(HgFileItem file)
        {
            if (_unviewedOnly && file.IsReviewed) return false;
            var path = NormalizePath(file.Path);
            if (_include.Count > 0 && !_include.Any(match => match(path))) return false;
            return !_exclude.Any(match => match(path));
        }

        /// <summary>Разбирает строку файла правил. Пустые строки и комментарии (#) дают null.</summary>
        public static HgCollapseRule Parse(string line)
        {
            var text = line?.Trim();
            if (string.IsNullOrEmpty(text) || text.StartsWith("#", StringComparison.Ordinal)) return null;

            var colon = text.IndexOf(':');
            var name = colon > 0 ? text.Substring(0, colon).Trim() : text;
            var expression = colon > 0 ? text.Substring(colon + 1).Trim() : text;
            return expression.Length == 0 ? null : new HgCollapseRule(name, expression);
        }

        private static Func<string, bool> CreateMatcher(string mask)
        {
            mask = NormalizePath(mask);
            if (mask.IndexOf('*') < 0 && mask.IndexOf('?') < 0)
                return path => path.IndexOf(mask, StringComparison.OrdinalIgnoreCase) >= 0;

            var regex = new Regex(
                "^" + Regex.Escape(mask).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return regex.IsMatch;
        }

        private static string NormalizePath(string path) => (path ?? "").Replace('/', '\\');
    }

    /// <summary>Файл правил сворачивания дерева. Хранится в профиле пользователя, вне репозиториев.</summary>
    public static class HgCollapseRules
    {
        public static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HgVs", "collapse-rules.txt");

        private const string Header =
            "# HgVs: rules for the Collapse button in Tree mode.\r\n" +
            "# Folders that contain needed files are expanded, all other folders are collapsed.\r\n" +
            "#\r\n" +
            "# One rule per line:  Name: expression\r\n" +
            "#   mask      needed if the path matches any such mask (no such masks = every file)\r\n" +
            "#   -mask     not needed if the path matches\r\n" +
            "#   unviewed  only files that are not marked as viewed\r\n" +
            "# A mask without * or ? matches any part of the path; with them it must match the whole path.\r\n" +
            "# Case is ignored, / and \\ are the same. Lines starting with # are ignored: use # to disable a rule.\r\n";

        private static readonly string[] DefaultRules =
        {
            @"Без тестов: -.Tests\ -.Test\",
            @"Непросмотренный код: unviewed -.Tests\ -.Test\",
            @"Непросмотренные: unviewed",
            @"Только тесты: .Tests\ .Test\",
            @"Без сгенерированного: -.Designer.cs -.g.cs -.resx",
            @"Только XAML: .xaml",
        };

        public static List<HgCollapseRule> Load()
        {
            if (!File.Exists(FilePath)) return new List<HgCollapseRule>();
            return File.ReadAllLines(FilePath, Encoding.UTF8)
                .Select(HgCollapseRule.Parse)
                .Where(rule => rule != null)
                .ToList();
        }

        /// <summary>Создаёт файл со справкой и без правил, если его ещё нет.</summary>
        public static void EnsureFileExists()
        {
            if (File.Exists(FilePath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, Header, new UTF8Encoding(true));
        }

        /// <summary>
        /// Дописывает правила по умолчанию, которых нет в файле. Правило считается имеющимся,
        /// если в файле есть правило с тем же именем, в том числе закомментированное.
        /// </summary>
        /// <returns>Количество добавленных правил.</returns>
        public static int AddDefaults()
        {
            EnsureFileExists();
            var existingNames = new HashSet<string>(
                File.ReadAllLines(FilePath, Encoding.UTF8)
                    .Select(line => HgCollapseRule.Parse(line.Trim().TrimStart('#')))
                    .Where(rule => rule != null)
                    .Select(rule => rule.Name),
                StringComparer.OrdinalIgnoreCase);

            var missing = DefaultRules.Where(line => !existingNames.Contains(HgCollapseRule.Parse(line).Name)).ToList();
            if (missing.Count == 0) return 0;

            var text = File.ReadAllText(FilePath, Encoding.UTF8);
            var prefix = text.Length == 0 || text.EndsWith("\n", StringComparison.Ordinal) ? "" : "\r\n";
            File.AppendAllText(FilePath, prefix + "\r\n" + string.Join("\r\n", missing) + "\r\n", new UTF8Encoding(false));
            return missing.Count;
        }
    }
}
