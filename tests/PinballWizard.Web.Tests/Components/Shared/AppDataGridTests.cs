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
}
