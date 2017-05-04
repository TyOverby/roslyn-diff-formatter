using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis;
using static Microsoft.CodeAnalysis.Formatting.Formatter;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;
using ShellProgressBar;

namespace CSharpFormatter
{

    using DocumentMap = Dictionary<string, (DocumentId id, List<DiffRequest> requests)>;

    struct DiffRequest
    {
        internal string filename;
        internal int lineStart;
        internal int lineCount;

        public DiffRequest(string filename, int lineStart, int lineCount)
        {
            this.filename = filename;
            this.lineStart = lineStart;
            this.lineCount = lineCount;
        }
    }


    class Program
    {
        // Builds a mapping of FileName -> (DocumentId, Requests[])
        // This grouping is later used to process each document at a time.
        static DocumentMap BuildDiffDocumentMap(Workspace workspace, Solution solution, IEnumerable<DiffRequest> requests)
        {
            var documentIdMap = new DocumentMap();
            foreach (var request in requests)
            {
                if (!(request.filename.EndsWith(".cs") || request.filename.EndsWith(".vb")))
                {
                    Console.WriteLine($"WARNING: {request.filename} is not a .cs or .vb file. Skipping");
                    continue;
                }

                if (documentIdMap.ContainsKey(request.filename))
                {
                    documentIdMap[request.filename].requests.Add(request);
                }
                else
                {
                    // Diff always uses unix separators, so we need to replace those with the system separator
                    var correctedFilename = request.filename.Replace('/', System.IO.Path.DirectorySeparatorChar);
                    var fullPath = System.IO.Path.Combine(Environment.CurrentDirectory, correctedFilename);
                    var documentIds = solution.GetDocumentIdsWithFilePath(fullPath);

                    if (!documentIds.IsDefaultOrEmpty)
                    {
                        documentIdMap.Add(request.filename, (documentIds.First(), new List<DiffRequest>() { request }));
                    }
                    else
                    {
                        Console.WriteLine($"WARNING: Could not find a document ID for {fullPath}");
                    }
                }

            }
            return documentIdMap;
        }

        static async Task<DocumentMap> BuildAllFilesDocumentMap(Solution solution)
        {
            // Get all documents. We don't want to format any generated resx files.
            var documents = solution.Projects
                .Where(p => p.HasDocuments)
                .SelectMany(p => p.Documents)
                .Where(d => d.Name.EndsWith("cs") || d.Name.EndsWith("vb"))
                .Where(d => !d.Name.EndsWith("Designer.cs") || !d.Name.EndsWith("Designer.vb"));

            var requests = new DocumentMap();

            foreach (var doc in documents)
            {
                var text = await doc.GetTextAsync();
                requests[doc.FilePath] = (doc.Id, new List<DiffRequest>() { new DiffRequest(doc.FilePath, 0, text.Lines.Count) });
            }

            return requests;
        }

        static async Task<int> ApplyChanges(MSBuildWorkspace workspace, Solution solution, DocumentMap documentMap, ProgressBar progress)
        {
            foreach (var kvp in documentMap)
            {

                progress.Tick($"formatting {kvp.Key}");
                var document = solution.GetDocument(kvp.Value.id);
                document = await ApplyChangesToDocumentAsync(document, kvp.Value.requests);
                solution = document.Project.Solution;
            }

            if (!workspace.TryApplyChanges(solution))
            {
                Console.WriteLine("ERROR: Failed while saving files to disk.");
                return 2;
            }
            else
            {
                return 0;
            }
        }

        // Returns the TextSpan for a set of contiguous lines in a document.
        static async Task<TextSpan> SpanForLinesAsync(Document document, int lineStart, int lineCount)
        {
            var text = await document.GetTextAsync();
            var includedLines = text.Lines.Skip(lineStart).Take(lineCount).ToArray();
            var firstLine = includedLines.First();
            var lastLine = includedLines.Last();
            var linesUnion = new TextSpan(firstLine.Start, lastLine.End - firstLine.Start);
            return linesUnion;
        }

        static async Task<Document> ApplyChangesToDocumentAsync(Document document, IEnumerable<DiffRequest> requests)
        {
            Console.WriteLine($"INFO: Applying changes to: {document.FilePath}");

            // Make sure that the reqeusts are in order from the top of the file
            // to the bottom.  This is important because text spans may shift during formatting, and we
            // need to be able to adjust them.
            requests = requests.OrderBy(r => r.lineStart);

            // lineAdjustment is the offset in line numbers that have been caused by a reformat.
            // Imagine a formatting that changes the number of lines in a document.  Now, the diff for
            // this file will be asking for a format on the wrong spans!  By tracking line-count changes
            // during formatting, we can correct the formatting requests to what they should be.
            int lineAdjustment = 0;

            foreach (var request in requests)
            {

                var span = await SpanForLinesAsync(document, request.lineStart + lineAdjustment, request.lineCount);
                var linesBeforeFormat = (await document.GetTextAsync()).Lines.Count();
                document = await FormatAsync(document, span);
                var linesAfterFormat = (await document.GetTextAsync()).Lines.Count();
                lineAdjustment += linesAfterFormat - linesBeforeFormat;
            }

            return document;
        }

        // THIS METHOD IS MY SHAME
        internal static IEnumerable<DiffRequest> ParseDiffs()
        {
            IEnumerable<string> ConsoleLines()
            {
                var line = "";
                while ((line = Console.ReadLine()) != null)
                {
                    yield return line;
                }
            }

            var list = new List<DiffRequest>();
            string currentFile = null;

            var lines = ConsoleLines().GetEnumerator();
            while (lines.MoveNext())
            {
                var controlLine = lines.Current;
                Parse:

                if (controlLine.StartsWith("+++"))
                {
                    currentFile = controlLine.Substring(6);
                }
                else if (controlLine.StartsWith("@@"))
                {
                    // Get rid of the starting @@
                    var stripped = controlLine.Substring(2);
                    // Find the next @@s
                    stripped = stripped.Substring(1, stripped.IndexOf("@@") - 1);
                    var lineinfo = stripped.Split(' ')[1].Substring(1).Split(',');
                    var lineStart = lineinfo[0];
                    var lineCount = lineinfo[1];

                    Debug.Assert(currentFile != null);

                    int ignoreFront = 0;
                    int ignoreEnd = 0;
                    while (lines.MoveNext() && lines.Current.StartsWith(" "))
                    {
                        ignoreFront += 1;
                    }
                    bool findEnd = true;
                    while (lines.MoveNext() && (lines.Current.StartsWith("+") || lines.Current.StartsWith("-")))
                    { }
                    findEnd = lines.Current.StartsWith(" ");
                    while (findEnd && lines.MoveNext() && lines.Current.StartsWith(" "))
                    {
                        ignoreEnd += 1;
                    }
                    if (findEnd)
                    {
                        ignoreEnd += 1;
                    }

                    var count = int.Parse(lineCount) - ignoreFront - ignoreEnd;
                    if (count != 0)
                    {
                        list.Add(new DiffRequest(currentFile, int.Parse(lineStart) + ignoreFront, count));
                    }

                    controlLine = lines.Current;
                    goto Parse;
                }
            }

            return list;
        }

        static async Task<int> AsyncMain(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("ERROR: you must provide a .sln file to the program");
                PrintHelp();
                return 1;
            }

            if (args.Length > 2)
            {
                Console.WriteLine("ERROR: Too many arguments");
                PrintHelp();
                return 1;
            }

            var allFiles = false;
            if (args.Length == 2)
            {
                if (args[1] != "-a")
                {
                    Console.WriteLine($"ERROR: Unrecognized argument {args[1]}");
                    PrintHelp();
                    return 1;
                }

                allFiles = true;
            }

            var workspace = MSBuildWorkspace.Create();
            Console.WriteLine($"INFO: Opening Solution: {args[0]} (This might take a while)");
            var solution = await workspace.OpenSolutionAsync(args[0]);

            DocumentMap documentMap;
            if (allFiles)
            {
                documentMap = await BuildAllFilesDocumentMap(solution);
            }
            else
            {
                var diffRequests = ParseDiffs();
                documentMap = BuildDiffDocumentMap(workspace, solution, diffRequests);
            }
            Console.WriteLine($"INFO: {documentMap.Count} files to process");

            using (var progress = new ProgressBar(documentMap.Count(), "", ConsoleColor.White))
            {
                return await ApplyChanges(workspace, solution, documentMap, progress);
            }
        }

        static int Main(string[] args)
        {
            return AsyncMain(args).GetAwaiter().GetResult();
        }

        static void PrintHelp()
        {
            Console.WriteLine($"Usage: RoslynDiffFormatter <solution file> -a (optional, format all files in solution instead of reading diff)");
        }
    }
}
