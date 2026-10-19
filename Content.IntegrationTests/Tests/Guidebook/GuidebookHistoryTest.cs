#nullable enable
using System.Collections.Generic;
using System.Linq;
using Content.Client.Guidebook.Controls;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared.Guidebook;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Guidebook;

/// <summary>
///     Checks the back and forward history of the guidebook window and that it keeps the tree selection and the
///     rules button in step with the shown guide.
/// </summary>
[TestFixture]
[TestOf(typeof(GuidebookWindow))]
public sealed class GuidebookHistoryTest : GameTest
{
    [SidedDependency(Side.Client)] private readonly IUserInterfaceManager _uiManager = null!;

    private static readonly ProtoId<GuideEntryPrototype> Survival = "Survival";
    private static readonly ProtoId<GuideEntryPrototype> Glossary = "Glossary";
    private static readonly ProtoId<GuideEntryPrototype> Engineering = "Engineering";
    private static readonly ProtoId<GuideEntryPrototype> Medical = "Medical";

    // The core rules entry, which is in the tree.
    private static readonly ProtoId<GuideEntryPrototype> CoreRules = "DefaultRuleset";

    // An individual rule page (ruleEntry: true) that is never the core rules entry, so it is left out of the tree.
    private static readonly ProtoId<GuideEntryPrototype> OtherRules = "RuleC1";

    private async Task<GuidebookWindow> CreateWindow(ProtoId<GuideEntryPrototype>? selected)
    {
        await Client.WaitIdleAsync();

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

    private async Task SelectInTree(GuidebookWindow window, ProtoId<GuideEntryPrototype> id)
    {
        await Client.WaitPost(() =>
        {
            var item = window.Tree.Items.Single(x => x.Metadata is GuideEntry entry && entry.Id == id);
            window.Tree.SetSelectedIndex(item.Index);
        });
        await RunTicksSync(2);
    }

    private static string? TreeSelection(GuidebookWindow window)
    {
        return (window.Tree.SelectedItem?.Metadata as GuideEntry)?.Id;
    }

    [Test]
    public async Task BackAndForwardFollowSelection()
    {
        var window = await CreateWindow(null);

        await SelectInTree(window, Survival);
        await SelectInTree(window, Glossary);
        await SelectInTree(window, Engineering);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.BackButton.Disabled, Is.False);
            Assert.That(window.ForwardButton.Disabled);
        });

        await Client.WaitPost(() => window.GoBack());
        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(Glossary));
            Assert.That(TreeSelection(window), Is.EqualTo(Glossary.Id));
            Assert.That(window.BackButton.Disabled, Is.False);
            Assert.That(window.ForwardButton.Disabled, Is.False);
        });

        await Client.WaitPost(() => window.GoBack());
        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(Survival));
            Assert.That(window.BackButton.Disabled);
            Assert.That(window.ForwardButton.Disabled, Is.False);
        });

        await Client.WaitPost(() => window.GoForward());
        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(Glossary));
            Assert.That(TreeSelection(window), Is.EqualTo(Glossary.Id));
        });

        await Client.WaitPost(() => window.Dispose());
    }

    [Test]
    public async Task NavigatingAfterBackDropsForwardEntries()
    {
        var window = await CreateWindow(null);

        await SelectInTree(window, Survival);
        await SelectInTree(window, Glossary);
        await SelectInTree(window, Engineering);

        await Client.WaitPost(() => window.GoBack());
        await Client.WaitPost(() => window.GoBack());
        await Client.WaitAssertion(() => Assert.That(window.Selected, Is.EqualTo(Survival)));

        await SelectInTree(window, Medical);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(Medical));
            Assert.That(window.ForwardButton.Disabled);
            Assert.That(window.BackButton.Disabled, Is.False);
        });

        await Client.WaitPost(() => window.GoBack());
        await Client.WaitAssertion(() => Assert.That(window.Selected, Is.EqualTo(Survival)));

        await Client.WaitPost(() => window.Dispose());
    }

    [Test]
    public async Task RulePageHistory()
    {
        var window = await CreateWindow(null);

        await SelectInTree(window, CoreRules);
        await Client.WaitPost(() => window.HandleClick(OtherRules));
        await RunTicksSync(2);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(OtherRules));
            Assert.That(window.ReturnContainer.Visible);
            Assert.That(TreeSelection(window), Is.EqualTo(CoreRules.Id));
        });

        await Client.WaitPost(() => window.GoBack());
        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(CoreRules));
            Assert.That(window.ReturnContainer.Visible, Is.False);
        });

        await Client.WaitPost(() => window.GoForward());
        await Client.WaitAssertion(() => Assert.That(window.Selected, Is.EqualTo(OtherRules)));

        await Client.WaitPost(() => window.GoHome());
        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(CoreRules));
            Assert.That(window.ReturnContainer.Visible, Is.False);
        });

        await SelectInTree(window, Survival);
        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(Survival));
            Assert.That(window.ReturnContainer.Visible, Is.False);
        });

        await Client.WaitPost(() => window.Dispose());
    }

    [Test]
    public async Task EntrySetChangeResetsHistory()
    {
        var window = await CreateWindow(null);

        await SelectInTree(window, Survival);
        await SelectInTree(window, Glossary);
        await Client.WaitAssertion(() => Assert.That(window.BackButton.Disabled, Is.False));

        await Client.WaitPost(() =>
        {
            var filtered = AllGuides()
                .Where(x => x.Key == Survival || x.Key == Glossary)
                .ToDictionary(x => x.Key, x => x.Value);
            window.UpdateGuides(filtered, selected: Survival);
        });
        await RunTicksSync(2);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(Survival));
            Assert.That(window.BackButton.Disabled);
            Assert.That(window.ForwardButton.Disabled);
        });

        await Client.WaitPost(() => window.Dispose());
    }

    [Test]
    public async Task UpdateGuidesKeepsRulePage()
    {
        var window = await CreateWindow(null);

        await SelectInTree(window, CoreRules);
        await Client.WaitPost(() => window.HandleClick(OtherRules));
        await RunTicksSync(2);

        await Client.WaitPost(() => window.UpdateGuides(AllGuides(), selected: OtherRules));
        await RunTicksSync(2);

        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(OtherRules));
            Assert.That(window.ReturnContainer.Visible);
        });

        await Client.WaitPost(() => window.Dispose());
    }

    [Test]
    public async Task TreeSelectionFollowsShownGuide()
    {
        var window = await CreateWindow(null);
        Dictionary<ProtoId<GuideEntryPrototype>, GuideEntry> filtered = default!;

        // Glossary is not under Survival, so it is not in the tree.
        await Client.WaitPost(() =>
        {
            filtered = AllGuides()
                .Where(x => x.Key == Survival || x.Key == Glossary)
                .ToDictionary(x => x.Key, x => x.Value);
            window.UpdateGuides(filtered, rootEntries: [Survival], selected: Survival);
        });
        await RunTicksSync(2);
        await Client.WaitAssertion(() => Assert.That(TreeSelection(window), Is.EqualTo(Survival.Id)));

        await Client.WaitPost(() => window.HandleClick(Glossary));
        await RunTicksSync(2);
        await Client.WaitAssertion(() =>
        {
            Assert.That(window.Selected, Is.EqualTo(Glossary));
            Assert.That(window.Tree.SelectedIndex, Is.Null);
        });

        await Client.WaitPost(() => window.Dispose());
    }
}
