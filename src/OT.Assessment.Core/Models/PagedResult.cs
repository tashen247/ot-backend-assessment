namespace OT.Assessment.Core.Models;

public sealed class PagedResult<T>
{
    public PagedResult(IReadOnlyList<T> data, int page, int pageSize, int total)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(total);

        Data = data;
        Page = page;
        PageSize = pageSize;
        Total = total;
    }

    public IReadOnlyList<T> Data { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int Total { get; }
    public int TotalPages => (int)Math.Ceiling(Total / (double)PageSize);
}
