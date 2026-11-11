using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The per-language comment test that decides whether a change touched code only in comments.
/// It is language-specific, so a line that is a comment in one language stays code in another:
/// <c>*p = x</c> is a dereference in C, Go and Rust, and <c>#include</c> is a directive in C.
/// </summary>
/// <remarks>
/// The input is one hunk's added and removed line contents, without the diff's <c>+</c> and
/// <c>-</c> prefix. A block comment is comment-only only when it opens and closes inside that
/// hunk; an unclosed opener is code, because the rest of the block lies outside what was measured.
/// </remarks>
public sealed class CommentSyntaxTests
{
    [Theory]
    [InlineData("Program.cs")]
    [InlineData("Program.java")]
    [InlineData("Program.kt")]
    [InlineData("Program.swift")]
    [InlineData("Program.go")]
    [InlineData("Program.rs")]
    [InlineData("Program.ts")]
    [InlineData("Program.js")]
    [InlineData("Program.c")]
    [InlineData("Program.cpp")]
    public void DoubleSlashLinesAreCommentOnlyInEachCStyleLanguage(string path)
    {
        Assert.True(CommentSyntax.IsCommentOnly(path, ["// a note"]), $"a // line in {path} is a comment.");
    }

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("Program.java")]
    [InlineData("Program.kt")]
    [InlineData("Program.swift")]
    [InlineData("Program.go")]
    [InlineData("Program.rs")]
    [InlineData("Program.ts")]
    [InlineData("Program.js")]
    [InlineData("Program.c")]
    [InlineData("Program.cpp")]
    public void BlockCommentOpenedAndClosedInOneHunkIsCommentOnly(string path)
    {
        Assert.True(CommentSyntax.IsCommentOnly(path, ["/*", " * a note", " */"]), $"a closed block in {path} is a comment.");
        Assert.True(CommentSyntax.IsCommentOnly(path, ["/* a note */"]), $"a one-line block in {path} is a comment.");
    }

    [Theory]
    [InlineData("settings.py")]
    [InlineData("build.sh")]
    [InlineData("pipeline.yml")]
    [InlineData("pipeline.yaml")]
    [InlineData("config.toml")]
    [InlineData("tool.rb")]
    [InlineData("script.ps1")]
    public void HashLinesAreCommentOnlyInEachHashLanguage(string path)
    {
        Assert.True(CommentSyntax.IsCommentOnly(path, ["# a note"]), $"a # line in {path} is a comment.");
    }

    [Fact]
    public void PowerShellBlockCommentOpenedAndClosedInOneHunkIsCommentOnly()
    {
        Assert.True(CommentSyntax.IsCommentOnly("script.ps1", ["<#", " a note", "#>"]), "a <# #> block is a PowerShell comment.");
    }

    [Theory]
    [InlineData("schema.sql")]
    [InlineData("init.lua")]
    public void DoubleDashLinesAreCommentOnlyInSqlAndLua(string path)
    {
        Assert.True(CommentSyntax.IsCommentOnly(path, ["-- a note"]), $"a -- line in {path} is a comment.");
    }

    [Theory]
    [InlineData("page.html")]
    [InlineData("data.xml")]
    [InlineData("view.xaml")]
    public void MarkupCommentsAreCommentOnlyInEachMarkupLanguage(string path)
    {
        Assert.True(CommentSyntax.IsCommentOnly(path, ["<!-- a note -->"]), $"a one-line comment in {path} is a comment.");
        Assert.True(CommentSyntax.IsCommentOnly(path, ["<!--", " a note", "-->"]), $"a multi-line comment in {path} is a comment.");
    }

    /// <summary>
    /// A blank line carries no code, and an indented comment is still a comment, so neither
    /// keeps a comment-only change out of the no-code-change set.
    /// </summary>
    [Fact]
    public void BlankAndIndentedCommentLinesAreCommentOnly()
    {
        Assert.True(CommentSyntax.IsCommentOnly("Program.cs", ["", "   "]), "blank lines carry no code.");
        Assert.True(CommentSyntax.IsCommentOnly("Program.cs", ["    // nested note"]), "an indented // line is still a comment.");
    }

    /// <summary>
    /// A trailing comment after code is code: the line starts with a code token, so the change
    /// is not comment-only even though the comment part alone would be.
    /// </summary>
    [Fact]
    public void ATrailingCommentAfterCodeIsNotCommentOnly()
    {
        Assert.False(CommentSyntax.IsCommentOnly("Program.cs", ["int x = 1; // note"]), "code followed by a comment is code.");
        Assert.False(CommentSyntax.IsCommentOnly("settings.py", ["x = 1  # note"]), "a Python assignment with a trailing comment is code.");
    }

    /// <summary>
    /// <c>#include</c> is a preprocessor directive in C, not a comment, so a hash line in a C file
    /// is code and never a comment-only change.
    /// </summary>
    [Fact]
    public void AHashLineInCIsCodeNotAComment()
    {
        Assert.False(CommentSyntax.IsCommentOnly("Program.c", ["#include <stdio.h>"]), "#include is a C directive.");
    }

    /// <summary>
    /// A star at the start of a line is a comment continuation only inside a block. Outside one,
    /// <c>*p = x</c> is a pointer dereference in C, Go and Rust, so it is code.
    /// </summary>
    [Theory]
    [InlineData("Program.c")]
    [InlineData("Program.go")]
    [InlineData("Program.rs")]
    public void AStarLineOutsideABlockIsCodeInCGoAndRust(string path)
    {
        Assert.False(CommentSyntax.IsCommentOnly(path, ["*p = x;"]), $"a dereference in {path} is code.");
    }

    /// <summary>
    /// A star continuation with no opener in the hunk is not inside a block that the hunk shows
    /// opening and closing, so it stays code.
    /// </summary>
    [Fact]
    public void AStarContinuationWithoutAnOpenerIsNotCommentOnly()
    {
        Assert.False(CommentSyntax.IsCommentOnly("Program.cs", [" * stray continuation"]), "a bare star line is not a comment.");
    }

    /// <summary>
    /// A block opened in the hunk and never closed there is not comment-only: the closing line
    /// lies outside what was measured, so the block cannot be shown to hold only comment.
    /// </summary>
    [Fact]
    public void ABlockOpenedButNotClosedInTheHunkIsNotCommentOnly()
    {
        Assert.False(CommentSyntax.IsCommentOnly("Program.cs", ["/*", " * a note"]), "an unclosed block may close outside the hunk.");
    }

    /// <summary>
    /// A change that mixes a comment with a code line is not comment-only, whatever the comment is.
    /// </summary>
    [Fact]
    public void AMixedChangeWithACodeLineIsNotCommentOnly()
    {
        Assert.False(CommentSyntax.IsCommentOnly("Program.cs", ["// a note", "int x = 1;"]), "one code line makes the change code.");
    }

    /// <summary>
    /// A path whose extension the table does not know is not comment-only, so a change in an
    /// unrecognised language is never silently skipped by a review lens.
    /// </summary>
    [Theory]
    [InlineData("data.xyz")]
    [InlineData("Makefile")]
    public void AnUnknownExtensionIsNeverCommentOnly(string path)
    {
        Assert.False(CommentSyntax.IsCommentOnly(path, ["// a note"]), $"{path} has no known comment syntax.");
    }
}
