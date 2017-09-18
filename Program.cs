using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using static Microsoft.CodeAnalysis.Formatting.Formatter;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;

namespace CSharpFormatter
{
    using DocumentMap = Dictionary<string, List<DiffRequest>>;

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
        static DocumentMap BuildDiffDocumentMap(IEnumerable<DiffRequest> requests)
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
                    documentIdMap[request.filename].Add(request);
                }
                else
                {
                    documentIdMap.Add(request.filename, new List<DiffRequest>() { request });
                }

            }
            return documentIdMap;
        }

        static void ApplyChanges(DocumentMap documentMap)
        {
            foreach (var kvp in documentMap)
            {
                string documentName = kvp.Key;
                var diffRequests = kvp.Value;
                try
                {
                    ApplyChangesToDocument(documentName, kvp.Value);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"EXCEPTION formatting {documentName}: {e.Message}");
                }
            }
        }

        // Returns the TextSpan for a set of contiguous lines in a document.
        static TextSpan SpanForLines(SourceText text, int lineStart, int lineCount)
        {
            var includedLines = text.Lines.Skip(lineStart).Take(lineCount).ToArray();
            var firstLine = includedLines.First();
            var lastLine = includedLines.Last();
            var linesUnion = new TextSpan(firstLine.Start, lastLine.End - firstLine.Start);
            return linesUnion;
        }

        static void ApplyChangesToDocument(string documentPath, IEnumerable<DiffRequest> requests)
        {
            if (documentPath.EndsWith("vb")) { return; }
            SyntaxNode after;
            using (var file = File.Open(documentPath, FileMode.Open))
            {
                var sourceText = SourceText.From(file);
                var tree = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseSyntaxTree(sourceText);

                // Make sure that the reqeusts are in order from the top of the file
                // to the bottom.  This is important because text spans may shift during formatting, and we
                // need to be able to adjust them.
                requests = requests.OrderBy(r => r.lineStart);
                var spans = requests.Select(req => SpanForLines(sourceText, req.lineStart, req.lineCount));
                after = Format(tree.GetRoot(), spans, new AdhocWorkspace());
            }

            File.WriteAllText(documentPath, after.ToFullString());
        }

        static IEnumerable<string> ConsoleLines()
        {
            var line = "";
            while ((line = Console.ReadLine()) != null)
            {
                yield return line;
            }
        }

        // THIS METHOD IS MY SHAME
        internal static IEnumerable<DiffRequest> ParseDiffs(IEnumerable<string> diffLines)
        {

            var list = new List<DiffRequest>();
            string currentFile = null;

            var lines = diffLine.GetEnumerator();
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

        static int Main(string[] args)
        {
            DocumentMap documentMap = BuildDiffDocumentMap(ParseDiffs(ConsoleLines()));
            Console.WriteLine($"INFO: {documentMap.Count} files to process");
            ApplyChanges(documentMap);
            return 0;
        }
    }
}

