#pragma warning disable CS0649
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

class CodeUtils
{
    static readonly string PROMPTS_DIR = "_prompts";
    static readonly string[] EXCLUDED_DIRS = { "bin", "obj", PROMPTS_DIR };
    static readonly string TODO = "TODO" + ":";
    static readonly string CSDEV = @"
you are a software engineer, experienced C# developer;
write only code after each prompt;
include implementations of any helper methods you introduce
";
    static readonly string PROMPT1 = @"
given C# code as helper, alredy implemented part, to be used as needed
";
    static readonly string PROMPT2 = @"
implement following method using instructions from TODO comments
";
    static readonly string PROMPT3 = @"
output only code with no additional explanation
";
    static readonly string PROMPT4 = @"
design abstract class following instructions from TODO comments
";

    public static void Prepare(string dir = ".")
    {
        dir = dir.TrimEnd('/');
        string promptDir = Path.Combine(dir, PROMPTS_DIR);
        if (Directory.Exists(promptDir))
            Directory.Delete(promptDir, true);
        Directory.CreateDirectory(promptDir);

        var classes = new List<ClassInfo>();

        WalkDirectory(classes, dir, dir, promptDir);
        GeneratePrompts(classes);
    }

    private static void GeneratePrompts(List<ClassInfo> classes)
    {
        var dups = classes.GroupBy(x => x.ClassName).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
        if (dups.Count > 0)
        {
            Console.WriteLine("Duplicate class names for prompt:");
            Console.WriteLine(string.Join(Environment.NewLine, dups));
            Environment.Exit(-1);
        }
        var types = classes.ToDictionary(x => x.ClassName);

        foreach (var ci in classes.Where(x => x.PromptFile != null))
        {
            if (ci.PromptFile.EndsWith(".class.txt"))
            {
                File.WriteAllText(ci.PromptFile, PROMPT4 + ci.ClassOnly);
                continue;
            }

            var usedTypes = new HashSet<string>
            {
                ci.ClassName
            };
            for (var i = 0; i < 2; i++)
            {
                var nestedTypes = new HashSet<string>();
                foreach (var t in usedTypes.Where(types.ContainsKey))
                {
                    nestedTypes.UnionWith(types[t].FieldTypes);
                    nestedTypes.UnionWith(types[t].MethodTypes);
                    nestedTypes.UnionWith(types[t].Interfaces);
                    if (types[t].BaseClass != null)
                        nestedTypes.Add(types[t].BaseClass);
                }
                usedTypes.UnionWith(nestedTypes);
            }
            usedTypes.Remove(ci.ClassName);

            var prompt = new StringBuilder();
            prompt.Append(PROMPT1);

            foreach (var t in usedTypes)
                if (types.ContainsKey(t))
                    prompt.AppendLine(types[t].ClassOnly);
            prompt.AppendLine(ci.ClassOnly);
            prompt.AppendLine(PROMPT2);
            prompt.AppendLine(String.Join(Environment.NewLine, ci.TodoMethods.Select(x => x.Source)));
            prompt.Append(PROMPT3);
            File.WriteAllText(ci.PromptFile, prompt.ToString());
        }
    }

    private static void WalkDirectory(List<ClassInfo> classes, string dir, string startDir, string promptDir, bool skip = false)
    {
        skip |= File.Exists(Path.Combine(dir, ".coderskip"));

        foreach (string subDir in Directory.GetDirectories(dir))
            if (!EXCLUDED_DIRS.Any(d => subDir.EndsWith(d)))
                WalkDirectory(classes, subDir, startDir, promptDir, skip);

        foreach (var oldfile in Directory.GetFiles(dir, "*.old"))
            File.Delete(oldfile);

        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            var fileContent = File.ReadAllText(file);

            if (skip && !fileContent.Contains(TODO))
                continue;

            var classContent = GetClassesInfo(fileContent);
            if (classContent == null)
                continue;
            var fileName = Path.Combine(promptDir, file[(startDir.Length + 1)..].Replace("/", "::"));
            var methodsFile = Path.ChangeExtension(fileName, ".txt");
            var classFile = Path.ChangeExtension(fileName, ".class.txt");

            for (var i = 0; i < classContent.Count; i++)
            {
                if (classContent[i].ClassOnly.Contains(TODO))
                {
                    classContent[i].PromptFile = classFile;
                    classFile = null;
                    continue;
                }
                if (classContent[i].TodoMethods.Count == 0)
                    continue;
                classContent[i].PromptFile = methodsFile;
                methodsFile = null;
            }
            classes.AddRange(classContent);
        }
    }

    public class TodoMethod
    {
        public string Name;
        public string Source;
        public List<string> Todos;
    }

    public static List<TodoMethod> GetTodoMethods(string fileContent)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(fileContent);
        var root = tree.GetRoot();

        var todoMethods = new List<TodoMethod>();

        var methodDeclarations = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

        foreach (var method in methodDeclarations)
        {
            var todos = new List<string>();
            var trivia = method.DescendantTrivia()
                .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia));

            foreach (var triv in trivia)
            {
                var comment = triv.ToString();
                if (comment.Contains(TODO))
                {
                    var todo = comment.Replace("//", "").Replace(TODO, "").Trim();
                    if (todo.Length < 3 && todos.Count == 0)
                        todo = "implement this method";
                    todos.Add(todo);
                }
            }

            if (todos.Count > 0)
            {
                todoMethods.Add(new TodoMethod
                {
                    Name = method.Identifier.Text,
                    Source = method.ToString(),
                    Todos = todos
                });
            }
        }

        return todoMethods;
    }

    public static string GetClassOnly(string fileContent)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(fileContent);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        var diagnostics = tree.GetDiagnostics();

        if (HasParsingErrors(diagnostics))
        {
            Console.WriteLine("Parsing errors found. Aborting operation.");
            foreach (var diag in diagnostics)
            {
                Console.WriteLine(diag.ToString());
            }
            return null;
        }

        var rewriter = new MethodBodyRewriter();
        var newRoot = rewriter.Visit(root);

        return newRoot.ToFullString();
    }

    public class ClassInfo
    {
        public string ClassName;
        public string BaseClass;
        public List<string> Interfaces;
        public List<string> FieldTypes;
        public List<string> MethodTypes;
        public string ClassOnly;
        public string FileClassOnly;
        public List<TodoMethod> TodoMethods;
        public string PromptFile;
    }

    public static List<ClassInfo> GetClassesInfo(string code)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code);
        var diagnostics = syntaxTree.GetDiagnostics();

        if (HasParsingErrors(diagnostics))
        {
            Console.WriteLine("Parsing errors found. Aborting operation.");
            foreach (var diag in diagnostics)
            {
                Console.WriteLine(diag.ToString());
            }
            return null;
        }
        var root = syntaxTree.GetRoot();
        var classNodes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
        var rewriter = new MethodBodyRewriter();
        var fileClassOnly = rewriter.Visit(root).ToFullString().Replace("{}", "");

        var classesInfo = new List<ClassInfo>();
        foreach (var classNode in classNodes)
        {
            var fieldTypes = new HashSet<string>();
            var fieldDeclarations = classNode.DescendantNodes().OfType<FieldDeclarationSyntax>();

            foreach (var fieldDeclaration in fieldDeclarations)
            {
                var typeSyntax = fieldDeclaration.Declaration.Type;
                fieldTypes.Add(typeSyntax.ToString());
            }

            var methodNodes = classNode.DescendantNodes().OfType<MethodDeclarationSyntax>();

            var typesUsed = new HashSet<string>();
            foreach (var methodNode in methodNodes)
            {
                typesUsed.Add(methodNode.ReturnType.ToString());

                foreach (var parameter in methodNode.ParameterList.Parameters)
                {
                    typesUsed.Add(parameter.Type.ToString());
                }
            }

            var classOnly = rewriter.Visit(classNode);

            var todoMethods = new List<TodoMethod>();
            var methodDeclarations = classNode.DescendantNodes().OfType<MethodDeclarationSyntax>();

            foreach (var method in methodDeclarations)
            {
                var todos = new List<string>();
                var trivia = method.DescendantTrivia()
                    .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia));

                foreach (var triv in trivia)
                {
                    var comment = triv.ToString();
                    if (comment.Contains(TODO))
                    {
                        todos.Add(comment.Replace("//", "").Trim());
                    }
                }

                if (todos.Count > 0)
                {
                    todoMethods.Add(new TodoMethod
                    {
                        Name = method.Identifier.Text,
                        Source = method.ToString(),
                        Todos = todos
                    });
                }
            }

            var classInfo = new ClassInfo
            {
                ClassName = classNode.Identifier.ValueText,
                BaseClass = classNode.BaseList?.Types.FirstOrDefault(type => type.IsKind(SyntaxKind.SimpleBaseType))?.ToString(),
                Interfaces = new List<string>(),
                FieldTypes = fieldTypes.ToList(),
                MethodTypes = typesUsed.ToList(),
                ClassOnly = classOnly.ToFullString().Replace("{}", ""),
                FileClassOnly = fileClassOnly,
                TodoMethods = todoMethods
            };

            foreach (var implementedInterface in classNode.BaseList?.Types.Where(type => !type.IsKind(SyntaxKind.SimpleBaseType)) ?? Enumerable.Empty<BaseTypeSyntax>())
            {
                classInfo.Interfaces.Add(implementedInterface.ToString());
            }

            classesInfo.Add(classInfo);
        }

        var interfaceNodes = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>();

        foreach (var interfaceNode in interfaceNodes)
        {
            var classInfo = new ClassInfo
            {
                ClassName = interfaceNode.Identifier.ValueText,
                ClassOnly = interfaceNode.ToFullString(),
                FileClassOnly = fileClassOnly
            };
            classesInfo.Add(classInfo);
        }

        return classesInfo;
    }

    private static bool HasParsingErrors(IEnumerable<Diagnostic> diagnostics)
    {
        return diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);
    }

    class MethodBodyRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var newBody = SyntaxFactory.Block();
            return node.WithBody(newBody)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }
    }

    private static SyntaxTree ParseCode(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        if (!HasParsingErrors(tree.GetDiagnostics()))
            return FormatAndAnnotateMethods(tree);
        string wrappedCode = $"class TemporaryClass {{ {code} }}";
        tree = CSharpSyntaxTree.ParseText(wrappedCode);
        if (!HasParsingErrors(tree.GetDiagnostics()))
            return FormatAndAnnotateMethods(tree);
        return tree;
    }

    private static SyntaxTree FormatAndAnnotateMethods(SyntaxTree tree)
    {
        SyntaxNode root = tree.GetRoot();

        // Annotate each method with a comment
        var rewriter = new CommentAppenderRewriter();
        root = rewriter.Visit(root);

        // Create a workspace and format the code
        var workspace = new AdhocWorkspace();
        var options = workspace.Options.WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.CSharp, 4)
                                       .WithChangedOption(FormattingOptions.UseTabs, LanguageNames.CSharp, false);
        SyntaxNode formattedRoot = Formatter.Format(root, workspace, options);

        // Return the new SyntaxTree
        return formattedRoot.SyntaxTree;
    }

    class CommentAppenderRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            // Create the comment to append
            SyntaxTrivia singleLineComment = SyntaxFactory.Comment("// DONE: review needed\n");

            // Append the comment to the start of the method body
            var newBody = node.Body.WithLeadingTrivia(node.Body.GetLeadingTrivia().Insert(0, singleLineComment));

            // Return the method with the new body
            return node.WithBody(newBody);
        }
    }

    public static string PreFilterCode(string code)
    {
        var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int startIndex = 0;

        foreach (var line in lines)
            if (line.StartsWith("using ") || string.IsNullOrWhiteSpace(line))
                startIndex++;
            else
                break;

        return string.Join(Environment.NewLine, lines, startIndex, lines.Length - startIndex);
    }

    public static void Process(string dir = ".")
    {
        string promptDir = Path.Combine(dir, PROMPTS_DIR);

        foreach (var file in Directory.GetFiles(promptDir, "*.txt"))
        {
            Console.Write($"Processing {file}... ");
            try
            {
                string promptContent = File.ReadAllText(file);
                string response = CallOpenAICompletionAPI(CSDEV, promptContent).Result;
                var codeBlocks = ExtractCode(response);
                StringBuilder output = new();
                StringBuilder next = new();

                if (file.Contains(".class."))
                {
                    next.AppendLine(string.Join(Environment.NewLine, codeBlocks));
                }
                else
                {
                    foreach (var rawcode in codeBlocks)
                    {
                        if (string.IsNullOrWhiteSpace(rawcode))
                            continue;

                        var code = PreFilterCode(rawcode);
                        if (string.IsNullOrWhiteSpace(code))
                            continue;

                        SyntaxTree syntaxTree = ParseCode(code);

                        if (HasParsingErrors(syntaxTree.GetDiagnostics()))
                        {
                            output.AppendLine(code);
                            output.AppendLine(string.Join(Environment.NewLine, syntaxTree.GetDiagnostics().Select(x => x.GetMessage())));
                            continue;
                        }

                        if (next.Length > 0)
                            next.AppendLine("#$#$#$#");
                        next.AppendLine(code);
                    }
                }

                if (output.Length > 0)
                    File.WriteAllText(Path.ChangeExtension(file, ".err"), output.ToString());
                if (next.Length > 0)
                    File.WriteAllText(Path.ChangeExtension(file, ".new"), next.ToString());
                Console.WriteLine("OK");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: {e}");
            }
        }
    }

    public static void Merge(string dir = ".")
    {
        string promptDir = Path.Combine(dir, PROMPTS_DIR);

        foreach (var nfile in Directory.GetFiles(promptDir, "*.new"))
        {
            var file = nfile.Replace(".class.", "");
            string codeContent = File.ReadAllText(file);
            var codeBlocks = codeContent.Split("#$#$#$#");
            string origFile = Path.Combine(dir, Path.ChangeExtension(file[(file.IndexOf(PROMPTS_DIR) + PROMPTS_DIR.Length + 1)..].Replace("::", "/"), ".cs"));
            string origCode = File.ReadAllText(origFile);
            var merged = origCode;

            Console.Write($"Merging {origFile}... ");

            try
            {
                if (nfile.Contains(".class."))
                {
                    merged = string.Join("", codeBlocks);
                }
                else
                {
                    foreach (var code in codeBlocks)
                    {
                        SyntaxTree syntaxTree = ParseCode(code);
                        merged = MergeBlock(merged, syntaxTree);
                    }
                }
                File.Move(origFile, Path.ChangeExtension(origFile, ".old"), true);
                File.WriteAllText(origFile, merged);
                File.Delete(nfile);
                File.Delete(Path.ChangeExtension(nfile, ".txt"));
                Console.WriteLine("OK");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: {e}");
            }
        }
    }

    private static List<string> ExtractCode(string response)
    {
        List<string> codeBlocks = new();

        string pattern = @"```csharp\r?\n(.*?)```";
        MatchCollection matches = Regex.Matches(response, pattern, RegexOptions.Singleline);

        foreach (Match match in matches.Cast<Match>())
        {
            if (match.Success)
            {
                string codeBlock = match.Groups[1].Value;
                codeBlocks.Add(codeBlock);
            }
        }
        return codeBlocks;
    }

    public static string MergeBlock(string fileContent, SyntaxTree newMethodsTree)
    {
        var tree = CSharpSyntaxTree.ParseText(fileContent);
        var root = tree.GetRoot();

        var methodDeclarations = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .ToList();

        var newMethodsRoot = newMethodsTree.GetRoot();
        var newMethodDeclarations = newMethodsRoot
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>();

        // Keep track of the last position to insert new methods that do not exist.
        string lastMethod = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Last().Identifier.ValueText;

        foreach (var newMethod in newMethodDeclarations)
        {
            var existingMethods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>().Where(x => x.Identifier.ValueText == newMethod.Identifier.ValueText);
            if (existingMethods.Any())
            {
                // Replace the body of the existing method with the new body.
                var existingMethod = existingMethods.First();
                root = root.ReplaceNode(
                    existingMethod,
                    newMethod
                        .WithLeadingTrivia(existingMethod.GetLeadingTrivia())
                        .WithTrailingTrivia(existingMethod.GetTrailingTrivia())
                );
            }
            else
            {
                // The method does not exist in the current content, so we need to append it.
                // Keep the position of the last method to ensure correct order of methods.
                var existingMethod = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>().Where(x => x.Identifier.ValueText == lastMethod).First();
                root = root.InsertNodesAfter(
                    existingMethod,
                    new[] { newMethod.WithLeadingTrivia(SyntaxFactory.EndOfLine(Environment.NewLine)) });
            }
            lastMethod = newMethod.Identifier.ValueText;
        }

        return root.ToFullString();
    }

    private const string API_URL = "https://api.openai.com/v1/chat/completions";
    private static string API_KEY;

    public static void GetApiKey()
    {
        string keyFilePath = "openai.key";
        string altKeyFilePath = "/usr/bin/openai.key";

        if (File.Exists(keyFilePath))
        {
            API_KEY = File.ReadAllText(keyFilePath).Trim();
        }
        else if (File.Exists(altKeyFilePath))
        {
            API_KEY = File.ReadAllText(altKeyFilePath).Trim();
        }
        Console.WriteLine("Cannot find openai.key!");
        Environment.Exit(-1);
    }

    static async Task ListModels()
    {
        using var client = new HttpClient();
        string modelsEndpoint = "https://api.openai.com/v1/models";

        try
        {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {API_KEY}");

            HttpResponseMessage response = await client.GetAsync(modelsEndpoint);
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();

            Console.WriteLine(responseBody);
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine("\nException Caught!");
            Console.WriteLine("Message :{0} ", e.Message);
        }
    }

    // https://platform.openai.com/docs/api-reference/introduction
    private static async Task<string> CallOpenAICompletionAPI(string role, string prompt)
    {
        using var httpClient = new HttpClient();

        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {API_KEY}");

        var requestData = new
        {
            model = "gpt-4-1106-preview",
            //model = "gpt-3.5-turbo-16k",
            messages = new[] {
                new { role = "system", content = role },
                new { role = "user", content = prompt }
            }
        };

        try
        {
            var response = await httpClient.PostAsync(API_URL,
                new StringContent(JsonSerializer.Serialize(requestData), Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var jsonObject = JsonDocument.Parse(jsonResponse).RootElement;
            return jsonObject.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Trim();
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }
        return null;
    }

}
