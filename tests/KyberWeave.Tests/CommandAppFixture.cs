using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Cli.Commands.Review;
using Spectre.Console.Cli;

namespace KyberWeave.Tests;

/// <summary>
/// The in-process CLI boundary the command-driving suites assemble when a case has to run a real
/// <see cref="CommandApp"/> rather than call a command instance.
/// </summary>
/// <remarks>
/// <para>
/// A CLI app is a wiring claim, not a fixture value: the branch names, the command names and the
/// application name are what a suite asserts the product registers. Assembling that shape
/// independently in each suite is what lets one suite test a differently composed CLI than
/// another, so a registration defect passes the one that still matches and fails the one that
/// does not. Building it once here means a registration change lands in every suite at the same
/// time, which is the property the suites are protecting rather than something they duplicate.
/// </para>
/// <para>
/// The presets are compositions, not conveniences. Each names a branch set a suite actually
/// drives, so a caller asks for "docs init plus review gates" instead of re-listing both
/// registrations and drifting from the suite beside it. What genuinely varies between presets —
/// whether parsing is strict, and which branches are registered — is what the private builder
/// takes. Nothing else is a parameter: a knob no caller turns is a second implementation waiting
/// to diverge.
/// </para>
/// </remarks>
internal static class CommandAppFixture
{
    /// <summary>
    /// The name the product's own entry point sets, so text rendered from a test names the real
    /// tool rather than the test assembly.
    /// </summary>
    /// <remarks>
    /// Every preset carries it, including the two suites that once omitted it. The name is read
    /// only by help and usage rendering — the product sets it in one place, <c>Program.cs</c> — and
    /// neither of those suites asserts on that text, so naming the app changes nothing either of
    /// them observes. A preset that left the name unset would be testing a differently branded app
    /// for no reason anyone could point at.
    /// </remarks>
    private const string ApplicationName = "kyber-weave";

    /// <summary>
    /// The <c>docs init</c> boundary: the <c>docs</c> branch and its single <c>init</c> command,
    /// with parsing left lax so an argument the command rejects reaches the command rather than
    /// being refused by the parser first.
    /// </summary>
    public static CommandApp DocsInit() => Build(strictParsing: false, DocsInitBranch);

    /// <summary>
    /// The council <c>review gates</c> boundary: the <c>review</c> branch and its single
    /// <c>gates</c> command, stricter than the product's own entry point so an option the command
    /// does not declare fails the case instead of being ignored.
    /// </summary>
    public static CommandApp ReviewGates() => Build(strictParsing: true, ReviewGatesBranch);

    /// <summary>
    /// The <c>review catalogue</c> boundary: the <c>review</c> branch and its single
    /// <c>catalogue</c> command, strict for the same reason as <see cref="ReviewGates"/>.
    /// </summary>
    public static CommandApp ReviewCatalogue() => Build(strictParsing: true, ReviewCatalogueBranch);

    /// <summary>
    /// Both branches at once, strict: <c>docs init</c> and the council <c>review gates</c> in one
    /// app, which is what a case needs when it scaffolds a repository and then gates it.
    /// </summary>
    public static CommandApp DocsInitWithReviewGates() =>
        Build(strictParsing: true, DocsInitBranch, ReviewGatesBranch);

    /// <summary>
    /// The one place a command app is assembled: the application name, whether parsing is strict,
    /// and the branches to register.
    /// </summary>
    private static CommandApp Build(bool strictParsing, params Action<IConfigurator>[] branches)
    {
        CommandApp app = new();
        app.Configure(config =>
        {
            config.SetApplicationName(ApplicationName);

            if (strictParsing)
                config.UseStrictParsing();

            foreach (Action<IConfigurator> branch in branches)
                branch(config);
        });

        return app;
    }

    private static void DocsInitBranch(IConfigurator config) =>
        config.AddBranch("docs", docs => { docs.AddCommand<DocsInitCommand>("init"); });

    private static void ReviewGatesBranch(IConfigurator config) =>
        config.AddBranch("review", review => { review.AddCommand<ReviewGatesCommand>("gates"); });

    private static void ReviewCatalogueBranch(IConfigurator config) =>
        config.AddBranch("review", review => { review.AddCommand<ReviewCatalogueCommand>("catalogue"); });
}
