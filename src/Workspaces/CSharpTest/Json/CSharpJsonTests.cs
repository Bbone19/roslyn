﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp.VirtualChars;
using Microsoft.CodeAnalysis.Json;
using Microsoft.CodeAnalysis.VirtualChars;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests.Json
{
    public partial class CSharpJsonParserTests
    {
        private readonly IVirtualCharService _service = CSharpVirtualCharService.Instance;
        private const string _statmentPrefix = "var v = ";

        private SyntaxToken GetStringToken(string text)
        {
            var statement = _statmentPrefix + text;
            var parsedStatement = SyntaxFactory.ParseStatement(statement);
            var token = parsedStatement.DescendantTokens().ToArray()[3];
            Assert.True(token.Kind() == SyntaxKind.StringLiteralToken);

            return token;
        }

        private void Test(string stringText,
            string expected, string looseDiagnostics, string strictDiagnostics,
            bool runLooseTreeCheck = true, bool runLooseSubTreeCheck = true,
            bool runStrictTreeCheck = true, bool runStrictSubTreeCheck = true,
            [CallerMemberName]string name = "")
        {
#if true
            if (runLooseSubTreeCheck || runLooseSubTreeCheck)
            {
                Test(stringText, strict: false, expected, looseDiagnostics, runLooseTreeCheck, runLooseSubTreeCheck);
            }
#endif

            if (runStrictTreeCheck || runStrictSubTreeCheck)
            {
                Test(stringText, strict: true, expected, strictDiagnostics, runStrictTreeCheck, runStrictSubTreeCheck);
            }
        }

        private void Test(
            string stringText, bool strict,
            string expectedTree, string expectedDiagnostics,
            bool runTreeCheck, bool runSubTreeChecks)
        {
            var tree = TryParseTree(stringText, strict, runTreeCheck, conversionFailureOk: false);

            // Tests are allowed to not run the subtree tests.  This is because some
            // subtrees can cause the native regex parser to exhibit very bad behavior
            // (like not ever actually finishing compiling).
            if (runSubTreeChecks)
            {
                // TryParseSubTrees(stringText, strict, runTreeCheck);
            }

            var actualTree = TreeToText(tree).Replace("\"", "\"\"");
            Assert.Equal(expectedTree.Replace("\"", "\"\""), actualTree);

            var actualDiagnostics = DiagnosticsToText(tree.Diagnostics).Replace("\"", "\"\"");
            Assert.Equal(expectedDiagnostics.Replace("\"", "\"\""), actualDiagnostics);
        }

        private void TryParseSubTrees(string stringText, bool strict, bool runTreeCheck)
        {
            // Trim the input from the right and make sure tree invariants hold
            var current = stringText;
            while (current != "@\"\"" && current != "\"\"")
            {
                current = current.Substring(0, current.Length - 2) + "\"";
                TryParseTree(current, strict, runTreeCheck, conversionFailureOk: true);
            }

            // Trim the input from the left and make sure tree invariants hold
            current = stringText;
            while (current != "@\"\"" && current != "\"\"")
            {
                if (current[0] == '@')
                {
                    current = "@\"" + current.Substring(3);
                }
                else
                {
                    current = "\"" + current.Substring(2);
                }

                TryParseTree(current, strict, runTreeCheck, conversionFailureOk: true);
            }

            for (int start = stringText[0] == '@' ? 2 : 1; start < stringText.Length - 1; start++)
            {
                TryParseTree(
                    stringText.Substring(0, start) +
                    stringText.Substring(start + 1, stringText.Length - (start + 1)),
                    strict, runTreeCheck,
                    conversionFailureOk: true);
            }
        }

        private (SyntaxToken, JsonTree, ImmutableArray<VirtualChar>) JustParseTree(
            string stringText, bool strict, bool conversionFailureOk)
        {
            var token = GetStringToken(stringText);
            if (token.ValueText == "")
            {
                return default;
            }

            var allChars = _service.TryConvertToVirtualChars(token);
            if (allChars.IsDefault)
            {
                Assert.True(conversionFailureOk, "Failed to convert text to token.");
                return (token, null, allChars);
            }

            var tree = JsonParser.TryParse(allChars, strict);
            return (token, tree, allChars);
        }

        private JsonTree TryParseTree(
            string stringText, bool strict, bool runTreeCheck, bool conversionFailureOk)
        {
            var (token, tree, allChars) = JustParseTree(stringText, strict, conversionFailureOk);
            if (tree == null)
            {
                Assert.True(allChars.IsDefault);
                return null;
            }

            //JavaScriptSerializer x = new JavaScriptSerializer();
            //x.DeserializeObject("{ x: 1 }");
            //var r = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes("{ \"x\": 1 } "),
            //    XmlDictionaryReaderQuotas.Max);
            //var v1 = r.Read();
            //var v2 = r.Read();
            //var d = new DataContractJsonSerializer(typeof(object));
            //var o = d.ReadObject(new MemoryStream(Encoding.UTF8.GetBytes("{ \"x\": 1 } ")));
            CheckInvariants(tree, allChars);

            if (runTreeCheck)
            {
                if (!strict)
                {
                    try
                    {
                        JToken.Parse(token.ValueText);
                    }
                    catch (Exception)
                    {
                        Assert.NotEmpty(tree.Diagnostics);
                        return tree;
                    }
                }
                else
                {
                    try
                    {
                        var serializer1 = new DataContractJsonSerializer(typeof(object));
                        serializer1.ReadObject(new MemoryStream(Encoding.UTF8.GetBytes(token.ValueText)));

                        var serializer2 = new JavaScriptSerializer();
                        serializer2.DeserializeObject(token.ValueText);
                    }
                    catch (Exception ex)
                    {
                        Assert.NotEmpty(tree.Diagnostics);
                        return tree;
                    }
                }

                Assert.Empty(tree.Diagnostics);
            }

            return tree;
        }

        private string TreeToText(JsonTree tree)
        {
            return new XElement("Tree",
                NodeToElement(tree.Root)).ToString();
        }

        private string DiagnosticsToText(ImmutableArray<JsonDiagnostic> diagnostics)
        { 
            if (diagnostics.IsEmpty)
            {
                return "";
            }

            return new XElement("Diagnostics",
                diagnostics.Select(d =>
                    new XElement("Diagnostic",
                        new XAttribute("Message", d.Message),
                        new XAttribute("Start", d.Span.Start),
                        new XAttribute("Length", d.Span.Length)))).ToString();
        }

        private XElement NodeToElement(JsonNode node)
        {
            var element = new XElement(node.Kind.ToString());
            foreach (var child in node)
            {
                element.Add(child.IsNode ? NodeToElement(child.Node) : TokenToElement(child.Token));
            }

            return element;
        }

        private XElement TokenToElement(JsonToken token)
        {
            var element = new XElement(token.Kind.ToString());

            if (token.Value != null)
            {
                element.Add(new XAttribute("value", token.Value));
            }

            if (token.LeadingTrivia.Length > 0)
            {
                element.Add(new XElement("Trivia", token.LeadingTrivia.Select(t => TriviaToElement(t))));
            }

            if (token.VirtualChars.Length > 0)
            {
                element.Add(token.VirtualChars.CreateString());
            }

            if (token.TrailingTrivia.Length > 0)
            {
                element.Add(new XElement("Trivia", token.TrailingTrivia.Select(t => TriviaToElement(t))));
            }

            return element;
        }

        private XElement TriviaToElement(JsonTrivia trivia)
            => new XElement(
                trivia.Kind.ToString(),
                trivia.VirtualChars.CreateString().Replace("\f", "\\f"));

        private void CheckInvariants(JsonTree tree, ImmutableArray<VirtualChar> allChars)
        {
            var root = tree.Root;
            var position = 0;
            CheckInvariants(root, ref position, allChars);
            Assert.Equal(allChars.Length, position);
        }

        private void CheckInvariants(JsonNode node, ref int position, ImmutableArray<VirtualChar> allChars)
        {
            foreach (var child in node)
            {
                if (child.IsNode)
                {
                    CheckInvariants(child.Node, ref position, allChars);
                }
                else
                {
                    CheckInvariants(child.Token, ref position, allChars);
                }
            }
        }

        private void CheckInvariants(JsonToken token, ref int position, ImmutableArray<VirtualChar> allChars)
        {
            CheckInvariants(token.LeadingTrivia, ref position, allChars);
            CheckCharacters(token.VirtualChars, ref position, allChars);
            CheckInvariants(token.TrailingTrivia, ref position, allChars);
        }

        private void CheckInvariants(ImmutableArray<JsonTrivia> leadingTrivia, ref int position, ImmutableArray<VirtualChar> allChars)
        {
            foreach (var trivia in leadingTrivia)
            {
                CheckInvariants(trivia, ref position, allChars);
            }
        }

        private void CheckInvariants(JsonTrivia trivia, ref int position, ImmutableArray<VirtualChar> allChars)
        {
            switch (trivia.Kind)
            {
                case JsonKind.SingleLineCommentTrivia:
                case JsonKind.MultiLineCommentTrivia:
                case JsonKind.WhitespaceTrivia:
                case JsonKind.EndOfLineTrivia:
                    break;
                default:
                    Assert.False(true, "Incorrect trivia kind");
                    return;
            }

            CheckCharacters(trivia.VirtualChars, ref position, allChars);
        }

        private static void CheckCharacters(ImmutableArray<VirtualChar> virtualChars, ref int position, ImmutableArray<VirtualChar> allChars)
        {
            for (var i = 0; i < virtualChars.Length; i++)
            {
                Assert.Equal(allChars[position + i], virtualChars[i]);
            }

            position += virtualChars.Length;
        }

        [Fact]
        public void TestDeepRecursion()
        {
            var (token, tree, chars) =
                JustParseTree(
@"@""[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[
[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[""",
strict: false, conversionFailureOk: false);
            Assert.False(token.IsMissing);
            Assert.False(chars.IsDefaultOrEmpty);
            Assert.Null(tree);
        }
    }
}
