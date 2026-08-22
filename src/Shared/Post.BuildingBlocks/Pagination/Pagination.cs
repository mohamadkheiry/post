namespace Post.BuildingBlocks.Pagination;

/// <summary>Validated one-based pagination request.</summary>
public sealed record PageRequest
{
    /// <summary>Default page size.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Default hard page-size ceiling.</summary>
    public const int DefaultMaximumPageSize = 200;

    private PageRequest(int pageNumber, int pageSize)
    {
        PageNumber = pageNumber;
        PageSize = pageSize;
    }

    /// <summary>One-based page number.</summary>
    public int PageNumber { get; }

    /// <summary>Requested item count.</summary>
    public int PageSize { get; }

    /// <summary>Zero-based row offset represented as a 64-bit number.</summary>
    public long Offset => (long)(PageNumber - 1) * PageSize;

    /// <summary>Creates a validated page request.</summary>
    public static PageRequest Create(
        int pageNumber = 1,
        int pageSize = DefaultPageSize,
        int maximumPageSize = DefaultMaximumPageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPageSize);
        if (pageSize > maximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, $"Page size cannot exceed {maximumPageSize}.");
        }

        return new PageRequest(pageNumber, pageSize);
    }
}

/// <summary>Immutable page plus total-count navigation metadata.</summary>
/// <typeparam name="TItem">Item type.</typeparam>
public sealed record PagedResult<TItem>
{
    internal PagedResult(IReadOnlyList<TItem> items, long totalCount, PageRequest page)
    {
        Items = items;
        TotalCount = totalCount;
        PageNumber = page.PageNumber;
        PageSize = page.PageSize;
        TotalPages = totalCount is 0 ? 0 : checked((long)Math.Ceiling(totalCount / (double)page.PageSize));
    }

    /// <summary>Items in this page.</summary>
    public IReadOnlyList<TItem> Items { get; }
    /// <summary>Total matching items.</summary>
    public long TotalCount { get; }
    /// <summary>Current one-based page.</summary>
    public int PageNumber { get; }
    /// <summary>Requested page size.</summary>
    public int PageSize { get; }
    /// <summary>Total number of pages.</summary>
    public long TotalPages { get; }
    /// <summary>Whether an earlier page exists.</summary>
    public bool HasPreviousPage => PageNumber > 1 && TotalCount > 0;
    /// <summary>Whether a later page exists.</summary>
    public bool HasNextPage => PageNumber < TotalPages;

    /// <summary>Maps page items without changing navigation metadata.</summary>
    public PagedResult<TNext> Map<TNext>(Func<TItem, TNext> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return PagedResult.Create(Items.Select(map).ToArray(), TotalCount, PageRequest.Create(PageNumber, PageSize, PageSize));
    }
}

/// <summary>Factory for immutable pages without static members on a generic type.</summary>
public static class PagedResult
{
    /// <summary>Creates a validated immutable page.</summary>
    public static PagedResult<TItem> Create<TItem>(IEnumerable<TItem> items, long totalCount, PageRequest page)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);

        var materialized = items as IReadOnlyList<TItem> ?? items.ToArray();
        if (materialized.Count > page.PageSize)
        {
            throw new ArgumentException("A page cannot contain more items than its requested page size.", nameof(items));
        }

        return new PagedResult<TItem>(materialized, totalCount, page);
    }
}
