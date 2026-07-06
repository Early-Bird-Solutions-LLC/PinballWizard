using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class AppDataGridTests : AsyncBunitContext
{
    public AppDataGridTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed record Row(string Name);
    private sealed record ThemedRow(string Name, List<string> Themes);

    private IRenderedComponent<IComponent> RenderGrid(
        IEnumerable<Row> items,
        bool showPager = true,
        int rowsPerPage = 25,
        string? testId = null)
    {
        return Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDataGrid<Row>>(1);
            builder.AddAttribute(2, "Items", items);
            builder.AddAttribute(3, "RowsPerPage", rowsPerPage);
            builder.AddAttribute(4, "ShowPager", showPager);
            if (testId is not null)
                builder.AddAttribute(5, "data-testid", testId);
            builder.AddAttribute(6, "Columns", (RenderFragment)(b =>
            {
                b.OpenComponent<PropertyColumn<Row, string>>(0);
                b.AddAttribute(1, "Property", (System.Linq.Expressions.Expression<Func<Row, string>>)(r => r.Name));
                b.AddAttribute(2, "Title", "Name");
                b.CloseComponent();
            }));
            builder.CloseComponent();
        });
    }

    private IRenderedComponent<IComponent> RenderThemedGrid(IEnumerable<ThemedRow> items)
    {
        return Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDataGrid<ThemedRow>>(1);
            builder.AddAttribute(2, "Items", items);
            builder.AddAttribute(6, "Columns", (RenderFragment)(b =>
            {
                b.OpenComponent<PropertyColumn<ThemedRow, string>>(0);
                b.AddAttribute(1, "Property", (System.Linq.Expressions.Expression<Func<ThemedRow, string>>)(r => r.Name));
                b.AddAttribute(2, "Title", "Name");
                b.CloseComponent();
            }));
            builder.CloseComponent();
        });
    }

    private static PinballWizard.Application.Ai.GridSearch.GridSearchResponse SemanticResponse(string query) =>
        new([], $"Searching for {query}", IsSemanticSearch: true, SemanticQuery: query);

    private static PinballWizard.Application.Ai.GridSearch.GridSearchResponse StructuralResponse(
        string column, string op, string value) =>
        new([new PinballWizard.Application.Ai.GridSearch.GridFilter(column, op, value)], "Filtered", false, null);

    [Fact]
    public void RendersItemsAsRows()
    {
        var items = new[] { new Row("Alpha"), new Row("Beta") };
        var cut = RenderGrid(items);
        var markup = cut.Markup;
        Assert.Contains("Alpha", markup);
        Assert.Contains("Beta", markup);
    }

    [Fact]
    public void ShowPagerTrue_RendersPagination()
    {
        var items = Enumerable.Range(1, 3).Select(i => new Row($"Row{i}")).ToList();
        var cut = RenderGrid(items, showPager: true);
        cut.Find(".mud-table-pagination");
    }

    [Fact]
    public void ShowPagerFalse_HidesPagination()
    {
        var items = new[] { new Row("Alpha") };
        var cut = RenderGrid(items, showPager: false);
        Assert.Empty(cut.FindAll(".mud-table-pagination"));
    }

    [Fact]
    public void SplatsDataTestId()
    {
        var items = new[] { new Row("Alpha") };
        var cut = RenderGrid(items, testId: "my-grid");
        cut.Find("[data-testid='my-grid']");
    }

    [Fact]
    public void SemanticSearch_MatchesOnStringProperty()
    {
        var items = new[] { new Row("Godzilla Pro"), new Row("Iron Man Pro") };
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDataGrid<Row>>(1);
            builder.AddAttribute(2, "Items", items);
            builder.AddAttribute(6, "Columns", (RenderFragment)(b =>
            {
                b.OpenComponent<PropertyColumn<Row, string>>(0);
                b.AddAttribute(1, "Property", (System.Linq.Expressions.Expression<Func<Row, string>>)(r => r.Name));
                b.AddAttribute(2, "Title", "Name");
                b.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var grid = cut.FindComponent<AppDataGrid<Row>>();

        grid.InvokeAsync(() => grid.Instance.GetType()
            .GetMethod("HandleAiFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(grid.Instance, [SemanticResponse("godzilla")]));

        Assert.Contains("Godzilla Pro", cut.Markup);
        Assert.DoesNotContain("Iron Man Pro", cut.Markup);
    }

    [Fact]
    public void SemanticSearch_MatchesOnIEnumerableStringProperty()
    {
        var items = new[]
        {
            new ThemedRow("Alien Invasion", ["sci-fi", "horror"]),
            new ThemedRow("Wild West Showdown", ["western"]),
        };
        var cut = RenderThemedGrid(items);
        var grid = cut.FindComponent<AppDataGrid<ThemedRow>>();

        grid.InvokeAsync(() => grid.Instance.GetType()
            .GetMethod("HandleAiFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(grid.Instance, [SemanticResponse("sci-fi")]));

        Assert.Contains("Alien Invasion", cut.Markup);
        Assert.DoesNotContain("Wild West Showdown", cut.Markup);
    }

    [Fact]
    public void SemanticSearch_CaseInsensitive_NoMatch_ShowsNoRows()
    {
        var items = new[] { new Row("Godzilla Pro") };
        var cut = RenderGrid(items);
        var grid = cut.FindComponent<AppDataGrid<Row>>();

        grid.InvokeAsync(() => grid.Instance.GetType()
            .GetMethod("HandleAiFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(grid.Instance, [SemanticResponse("nonexistent-theme")]));

        Assert.DoesNotContain("Godzilla Pro", cut.Markup);
    }

    [Fact]
    public void StructuralFilter_StillAppliesWhenNotSemanticSearch()
    {
        var items = new[] { new Row("Alpha"), new Row("Beta") };
        var cut = RenderGrid(items);
        var grid = cut.FindComponent<AppDataGrid<Row>>();

        grid.InvokeAsync(() => grid.Instance.GetType()
            .GetMethod("HandleAiFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(grid.Instance, [StructuralResponse("Name", "equals", "Alpha")]));

        Assert.Contains("Alpha", cut.Markup);
        Assert.DoesNotContain("Beta", cut.Markup);
    }

    [Fact]
    public void ClearFeedback_ResetsAppliedFilter()
    {
        var items = new[] { new Row("Alpha"), new Row("Beta") };
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDataGrid<Row>>(1);
            builder.AddAttribute(2, "Items", items);
            builder.AddAttribute(3, "SearchContext", "test-context");
            builder.AddAttribute(6, "Columns", (RenderFragment)(b =>
            {
                b.OpenComponent<PropertyColumn<Row, string>>(0);
                b.AddAttribute(1, "Property", (System.Linq.Expressions.Expression<Func<Row, string>>)(r => r.Name));
                b.AddAttribute(2, "Title", "Name");
                b.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var gridSearch = cut.FindComponent<PinballWizard.Web.Components.Shared.GridSearch>();

        // Apply a structural filter directly on the grid — this is the same state
        // GridSearch would produce by calling its bound OnFiltersApplied callback,
        // so exercising it here (rather than driving the search box's own UI) keeps
        // the test focused on the ClearFeedback → OnFiltersApplied contract.
        var grid = cut.FindComponent<AppDataGrid<Row>>();
        grid.InvokeAsync(() => grid.Instance.GetType()
            .GetMethod("HandleAiFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(grid.Instance, [StructuralResponse("Name", "equals", "Alpha")]));
        Assert.DoesNotContain("Beta", cut.Markup);

        // Dismissing the feedback banner (ClearFeedback) must un-filter the grid.
        gridSearch.InvokeAsync(() => gridSearch.Instance.GetType()
            .GetMethod("ClearFeedback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(gridSearch.Instance, null));

        Assert.Contains("Beta", cut.Markup);
    }
}
