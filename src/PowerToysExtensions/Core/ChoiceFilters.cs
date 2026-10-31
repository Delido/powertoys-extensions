using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerToysExtensions.Core;

/// <summary>The filter dropdown of a list page (Raycast's searchBarAccessory dropdown).</summary>
public sealed partial class ChoiceFilters : Filters
{
    private readonly (string Id, string Name, IconInfo? Icon)[] _choices;

    public ChoiceFilters(params (string Id, string Name)[] choices)
        : this([.. choices.Select(c => (c.Id, c.Name, (IconInfo?)null))])
    {
    }

    public ChoiceFilters((string Id, string Name, IconInfo? Icon)[] choices)
    {
        _choices = choices;
        CurrentFilterId = choices[0].Id;
        PropChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CurrentFilterId))
            {
                Changed?.Invoke();
            }
        };
    }

    /// <summary>Raised when the user picks another filter.</summary>
    public event Action? Changed;

    public override IFilterItem[] GetFilters() =>
        [.. _choices.Select(c => (IFilterItem)new Filter { Id = c.Id, Name = c.Name, Icon = c.Icon ?? new IconInfo(string.Empty) })];
}
