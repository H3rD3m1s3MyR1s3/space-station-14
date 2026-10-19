#nullable enable
using System.Collections.Generic;
using System.Linq;
using Content.Client.Guidebook.Controls;
using Content.Client.UserInterface.Controls.FancyTree;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared.CCVar;
using Content.Shared.Guidebook;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Guidebook;

/// <summary>
///     Checks the bookmark list of the guidebook window: loading, saving, selecting and syncing with the main tree.
/// </summary>
[TestFixture]
[TestOf(typeof(GuidebookWindow))]
public sealed class GuidebookBookmarkTest : GameTest
{
    [SidedDependency(Side.Client)] private readonly IUserInterfaceManager _uiManager = null!;
    [SidedDependency(Side.Client)] private readonly IConfigurationManager _cfg = null!;

    private static readonly ProtoId<GuideEntryPrototype> Survival = "Survival";
    private static readonly ProtoId<GuideEntryPrototype> Glossary = "Glossary";

    // The core rules entry, which is in the main tree.
    private static readonly ProtoId<GuideEntryPrototype> CoreRules = "DefaultRuleset";

    // An individual rule page (ruleEntry: true), which is left out of the main tree.
    private static readonly ProtoId<GuideEntryPrototype> OtherRules = "RuleC1";

    private async Task<GuidebookWindow> CreateWindow(string bookmarks, ProtoId<GuideEntryPrototype>? selected)
    {
        await Client.WaitIdleAsync();
        await OverrideCVar(Side.Client, CCVars.GuidebookBookmarks, bookmarks, sync: false);

        GuidebookWindow window = default!;
        await Client.WaitPost(() =>
        {
            window = _uiManager.CreateWindow<GuidebookWindow>();
            window.UpdateGuides(AllGuides(), selected: selected);
            window.OpenCentered();
        });
        await RunTicksSync(5);
        return window;
    }

    private Dictionary<ProtoId<GuideEntryPrototype>, GuideEntry> AllGuides()
    {
        return CProtoMan.EnumeratePrototypes<GuideEntryPrototype>()
            .ToDictionary(x => new ProtoId<GuideEntryPrototype>(x.ID), x => (GuideEntry) x);
    }

    private static int BookmarkIndex(FancyTree tree, ProtoId<GuideEntryPrototype> id)
    {
        return tree.Items.Single(x => x.Metadata is GuideEntry entry && entry.Id == id).Index;
    }

    private static string? SelectedBookmarkId(FancyTree tree)
    {
        return (tree.SelectedItem?.Metadata as GuideEntry)?.Id;
    }

    [Test]
    public async Task BookmarksPersistInCVar()
    {
        var window = await CreateWindow("Survival,Glossary,DoesNotExistGuide", Survival);

        await Client.WaitAssertion(() =>
        {
            var tree = window.BookmarkTree;
            Assert.That(tree.Visible);
            Assert.That(tree.Items, Has.Count.EqualTo(3));
            Assert.That(tree.Items[0].Metadata, Is.Null, "First item should be the header.");

            var ids = tree.Items.Skip(1).Select(x => ((GuideEntry) x.Metadata!).Id).ToList();
            Assert.That(ids, Is.EqualTo(new[] { Survival.Id, Glossary.Id }));
            Assert.That(window.BookmarkButton.Pressed);
        });

        await Client.WaitPost(() => window.SetBookmarked(false));
        await RunTicksSync(2);

        await Client.WaitAssertion(() =>
        {
            Assert.That(_cfg.GetCVar(CCVars.GuidebookBookmarks), Is.EqualTo("Glossary,DoesNotExistGuide"));
            Assert.That(window.BookmarkTree.Items, Has.Count.EqualTo(2));
            Assert.That(((GuideEntry) window.BookmarkTree.Items[1].Metadata!).Id, Is.EqualTo(Glossary.Id));
            Assert.That(window.BookmarkButton.Pressed, Is.False);
        });

        await Client.WaitPost(() => window.SetBookmarked(true));
        await RunTicksSync(2);

        await Client.WaitAssertion(() =>
        {
            Assert.That(_cfg.GetCVar(CCVars.GuidebookBookmarks), Is.EqualTo("Glossary,DoesNotExistGuide,Survival"));
            Assert.That(window.BookmarkButton.Pressed);
        });

        await Client.WaitPost(() => window.Dispose());
    }

    [Test]
    public async Task SelectingBookmarkShowsGuideAndSyncsSelection()
    {
        var window = await CreateWindow("Survival,Glossary", Survival);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(Survival));
            Assert.That(SelectedBookmarkId(window.BookmarkTree), Is.EqualTo(Survival.Id));
            Assert.That(window.BookmarkButton.Pressed);
        });

        await Client.WaitPost(() =>
            window.BookmarkTree.SetSelectedIndex(BookmarkIndex(window.BookmarkTree, Glossary)));
        await RunTicksSync(2);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(Glossary));
            Assert.That((window.Tree.SelectedItem?.Metadata as GuideEntry)?.Id, Is.EqualTo(Glossary.Id));
            Assert.That(SelectedBookmarkId(window.BookmarkTree), Is.EqualTo(Glossary.Id));
            Assert.That(window.BookmarkButton.Pressed);
        });

        // Selecting in the main tree moves the bookmark highlight.
        await Client.WaitPost(() =>
        {
            var item = window.Tree.Items.Single(x => x.Metadata is GuideEntry e && e.Id == Survival);
            window.Tree.SetSelectedIndex(item.Index);
        });
        await RunTicksSync(2);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(Survival));
            Assert.That(SelectedBookmarkId(window.BookmarkTree), Is.EqualTo(Survival.Id));
        });

        // A guide that is not bookmarked clears the highlight.
        await Client.WaitPost(() =>
        {
            var item = window.Tree.Items.First(x =>
                x.Metadata is GuideEntry e && e.Id != Survival && e.Id != Glossary && !e.RuleEntry);
            window.Tree.SetSelectedIndex(item.Index);
        });
        await RunTicksSync(2);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.BookmarkTree.SelectedIndex, Is.Null);
            Assert.That(window.BookmarkButton.Pressed, Is.False);
        });

        await Client.WaitPost(() => window.Dispose());
    }

    [Test]
    public async Task OnlyGuidesInTreeAreBookmarkable()
    {
        var window = await CreateWindow("RuleC1,Survival,DefaultRuleset", Survival);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Tree.TryGetIndexFromMetadata(AllGuides()[OtherRules], out _), Is.False,
                "The test needs a guide that is not in the main tree.");
            var ids = window.BookmarkTree.Items.Skip(1).Select(x => ((GuideEntry) x.Metadata!).Id).ToList();
            Assert.That(ids, Is.EqualTo(new[] { Survival.Id, CoreRules.Id }));
        });

        await Client.WaitPost(() => window.HandleClick(OtherRules));
        await RunTicksSync(2);
        await Client.WaitAssertion(() => Assert.That(window.BookmarkButton.Visible, Is.False));

        await Client.WaitPost(() =>
            window.BookmarkTree.SetSelectedIndex(BookmarkIndex(window.BookmarkTree, CoreRules)));
        await RunTicksSync(2);
        await Client.WaitAssertion(() => Assert.That(window.Selected, Is.EqualTo(CoreRules)));

        await Client.WaitPost(() => window.HandleClick(Survival));
        await RunTicksSync(2);
        await Client.WaitAssertion(() => Assert.That(window.BookmarkButton.Visible));

        await Client.WaitPost(() => window.Dispose());
    }

    [Test]
    public async Task HeaderRowCollapsesListAndStateSurvivesRebuild()
    {
        var window = await CreateWindow("Survival,Glossary", Survival);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.BookmarkTree.Items[0].Expanded);
            Assert.That(SelectedBookmarkId(window.BookmarkTree), Is.EqualTo(Survival.Id));
        });

        await Client.WaitPost(() => window.BookmarkTree.SetSelectedIndex(0));
        await RunTicksSync(2);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.BookmarkTree.Items[0].Expanded, Is.False);
            Assert.That(SelectedBookmarkId(window.BookmarkTree), Is.EqualTo(Survival.Id));
            Assert.That(window.Selected, Is.EqualTo(Survival));
        });

        await Client.WaitPost(() => window.HandleClick(Glossary));
        await RunTicksSync(2);
        await Client.WaitPost(() => window.SetBookmarked(false));
        await RunTicksSync(2);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.BookmarkTree.Items, Has.Count.EqualTo(2));
            Assert.That(window.BookmarkTree.Items[0].Expanded, Is.False);
        });

        await Client.WaitPost(() => window.BookmarkTree.SetSelectedIndex(0));
        await RunTicksSync(2);

        await Client.WaitAssertion(() => Assert.That(window.BookmarkTree.Items[0].Expanded));

        await Client.WaitPost(() => window.Dispose());
    }
}
