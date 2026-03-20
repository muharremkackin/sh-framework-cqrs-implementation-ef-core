using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace SH.Framework.Library.Cqrs.Implementation.EntityFrameworkCore;

public static class ListRequestExtensions
{
    public static IQueryable<TEntity> Apply<TEntity, TResponse>(this IQueryable<TEntity> query,
        ListRequest<TResponse> request)
    {
        return query
            .ApplyFilters<TEntity>(request.Filters)
            .ApplyOrders<TEntity>(request.Orders);
    }

    public static IQueryable<TEntity> ApplyWithPaging<TEntity, TResponse>(this IQueryable<TEntity> query,
        ListRequest<TResponse> request)
    {
        return query
            .Apply(request)
            .ApplyPaging(request.Page, request.PageSize);
    }

    public static async Task<ListResult<TEntity>> ToListResultAsync<TEntity, TResponse>(this IQueryable<TEntity> query,
        ListRequest<TResponse> request, CancellationToken ct = default)
    {
        var filtered = query.Apply(request);
        var count = await filtered.CountAsync(ct);
        var items = await filtered
            .ApplyPaging(request.Page, request.PageSize)
            .ToListAsync(ct);

        return ListResult<TEntity>.Create(items, count, request.Page, request.PageSize.ValidatePageSize());
    }

    public static async Task<ListResult<TResponse>> ToListResultAsync<TEntity, TResponse>(
        this IQueryable<TEntity> query, ListRequest<TResponse> request,
        Expression<Func<TEntity, TResponse>> selector, CancellationToken ct = default)
    {
        var filtered = query.Apply(request);
        var count = await filtered.CountAsync(ct);
        var items = await filtered
            .ApplyPaging(request.Page, request.PageSize)
            .Select(selector)
            .ToListAsync(ct);

        return ListResult<TResponse>.Create(items, count, request.Page, request.PageSize.ValidatePageSize());
    }

    public static IQueryable<TEntity> ApplyPaging<TEntity>(this IQueryable<TEntity> query, int page, int pageSize)
    {
        if (page <= 0) page = 1;
        pageSize = pageSize.ValidatePageSize();

        return query.Skip((page - 1) * pageSize).Take(pageSize);
    }


    public static IQueryable<TEntity> ApplyOrders<TEntity>(this IQueryable<TEntity> query,
        List<ListRequestOrder>? orders)
    {
        if (orders == null) return query;

        IOrderedQueryable<TEntity>? orderedQuery = null;

        foreach (var order in orders)
        {
            if (string.IsNullOrWhiteSpace(order.Field)) continue;

            var property = GetPropertyInfo<TEntity>(order.Field);
            if (property == null) continue;

            var parameter = Expression.Parameter(typeof(TEntity), "x");
            var access = Expression.Property(parameter, property);
            var lambda = Expression.Lambda(access, parameter);

            var methodName = orderedQuery == null
                ? order.GetDirection() == ListRequestOrder.OrderDirection.Asc ? "OrderBy" : "OrderByDescending"
                : order.GetDirection() == ListRequestOrder.OrderDirection.Asc
                    ? "ThenBy"
                    : "ThenByDescending";

            var method = typeof(Queryable).GetMethods()
                .First(m => m.Name == methodName && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(TEntity), property.PropertyType);

            orderedQuery = (IOrderedQueryable<TEntity>)method.Invoke(null, [orderedQuery ?? query, lambda])!;
        }

        return orderedQuery ?? query;
    }

    public static IQueryable<TEntity> ApplyFilters<TEntity>(this IQueryable<TEntity> query,
        List<ListRequestFilter>? filters)
    {
        if (filters == null || filters.Count == 0) return query;

        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Field)) continue;

            var expression = BuildFilterExpression<TEntity>(filter);
            if (expression != null) query = query.Where(expression);
        }

        return query;
    }

    private static int ValidatePageSize(this int pageSize)
    {
        if (pageSize <= 0) pageSize = 10;
        if (pageSize > 1000) pageSize = 1000;
        
        return pageSize;
    }

    private static Expression<Func<TEntity, bool>>? BuildFilterExpression<TEntity>(ListRequestFilter filter)
    {
        var property = GetPropertyInfo<TEntity>(filter.Field);
        if (property == null) return null;

        var parameter = Expression.Parameter(typeof(TEntity), "x");
        var access = Expression.Property(parameter, property);

        Expression? body;

        try
        {
            body = filter.GetOperator() switch
            {
                ListRequestFilter.FilterOperator.Equals => BuildComparisonExpression(access, filter.Value,
                    Expression.Equal),
                ListRequestFilter.FilterOperator.NotEquals => BuildComparisonExpression(access, filter.Value,
                    Expression.NotEqual),
                ListRequestFilter.FilterOperator.GreaterThan => BuildComparisonExpression(access, filter.Value,
                    Expression.GreaterThan),
                ListRequestFilter.FilterOperator.GreaterThanOrEqual => BuildComparisonExpression(access, filter.Value,
                    Expression.GreaterThanOrEqual),
                ListRequestFilter.FilterOperator.LessThan => BuildComparisonExpression(access, filter.Value,
                    Expression.LessThan),
                ListRequestFilter.FilterOperator.LessThanOrEqual => BuildComparisonExpression(access, filter.Value,
                    Expression.LessThanOrEqual),
                ListRequestFilter.FilterOperator.Contains => BuildStringMethodExpression(access, filter.Value,
                    "Contains"),
                ListRequestFilter.FilterOperator.NotContains => BuildNotExpression(
                    BuildStringMethodExpression(access, filter.Value, "Contains")),
                ListRequestFilter.FilterOperator.StartsWith => BuildStringMethodExpression(access, filter.Value,
                    "StartsWith"),
                ListRequestFilter.FilterOperator.NotStartsWith => BuildNotExpression(
                    BuildStringMethodExpression(access, filter.Value, "StartsWith")),
                ListRequestFilter.FilterOperator.EndsWith => BuildStringMethodExpression(access, filter.Value,
                    "EndsWith"),
                ListRequestFilter.FilterOperator.NotEndsWith => BuildNotExpression(
                    BuildStringMethodExpression(access, filter.Value, "EndsWith")),
                ListRequestFilter.FilterOperator.Between => BuildBetweenExpression(access, filter.Value),
                ListRequestFilter.FilterOperator.NotBetween => BuildNotExpression(
                    BuildBetweenExpression(access, filter.Value)),
                ListRequestFilter.FilterOperator.IsNull => Expression.Equal(access,
                    Expression.Constant(null, property.PropertyType)),
                ListRequestFilter.FilterOperator.IsNotNull => BuildNotExpression(Expression.Equal(access,
                    Expression.Constant(null, property.PropertyType))),
                ListRequestFilter.FilterOperator.IsEmpty => BuildIsEmptyExpression(access),
                ListRequestFilter.FilterOperator.IsNotEmpty => BuildNotExpression(BuildIsEmptyExpression(access)),
                ListRequestFilter.FilterOperator.In => BuildInExpression(access, filter.Value),
                ListRequestFilter.FilterOperator.NotIn => BuildNotExpression(BuildInExpression(access, filter.Value)),
                _ => null
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (body == null) return null;

        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    private static PropertyInfo? GetPropertyInfo<TEntity>(string propertyName)
    {
        return typeof(TEntity).GetProperty(propertyName,
            BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
    }

    private static Expression? BuildComparisonExpression(MemberExpression property, string? value,
        Func<Expression, Expression, BinaryExpression> comparison)
    {
        var converted = ConvertValue(value, property.Type);
        if (converted == null && value != null) return null;

        var constant = Expression.Constant(converted, property.Type);
        return comparison(property, constant);
    }

    private static Expression? BuildStringMethodExpression(MemberExpression property, string? value, string methodName)
    {
        if (property.Type != typeof(string)) return null;

        if (value == null) return null;

        var method = typeof(string).GetMethod(methodName, [typeof(string)])!;
        var constant = Expression.Constant(value);

        var nullCheck = Expression.NotEqual(property, Expression.Constant(null, typeof(string)));
        var call = Expression.Call(property, method, constant);

        return Expression.AndAlso(nullCheck, call);
    }

    private static Expression? BuildBetweenExpression(MemberExpression property, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var parts = value.Split(",");
        if (parts.Length != 2) return null;

        var min = ConvertValue(parts[0].Trim(), property.Type);
        var max = ConvertValue(parts[1].Trim(), property.Type);

        if (min == null || max == null) return null;

        var minConstant = Expression.Constant(min, property.Type);
        var maxConstant = Expression.Constant(max, property.Type);

        var greaterThanOrEqual = Expression.GreaterThanOrEqual(property, minConstant);
        var lessThanOrEqual = Expression.LessThanOrEqual(property, maxConstant);

        return Expression.AndAlso(greaterThanOrEqual, lessThanOrEqual);
    }

    private static Expression? BuildIsEmptyExpression(MemberExpression property)
    {
        if (property.Type != typeof(string)) return null;

        var nullCheck = Expression.Equal(property, Expression.Constant(null, typeof(string)));
        var emptyCheck = Expression.Equal(property, Expression.Constant(string.Empty));

        return Expression.OrElse(nullCheck, emptyCheck);
    }

    private static Expression? BuildInExpression(MemberExpression property, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var values = value.Split(",")
            .Select(v => ConvertValue(v.Trim(), property.Type))
            .Where(v => v != null)
            .ToList();

        if (values.Count == 0) return null;

        var listType = typeof(List<>).MakeGenericType(property.Type);
        var list = Activator.CreateInstance(listType);
        var addMethod = listType.GetMethod("Add");

        foreach (var v in values) addMethod?.Invoke(list, [v]);

        var contains = listType.GetMethod("Contains")!;
        var constant = Expression.Constant(list);

        return Expression.Call(constant, contains, property);
    }

    private static Expression? BuildNotExpression(Expression? expression)
    {
        return expression == null ? null : Expression.Not(expression);
    }


    private static object? ConvertValue(string? value, Type targetType)
    {
        if (value == null) return null;

        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        try
        {
            if (underlyingType == typeof(Guid)) return Guid.TryParse(value, out var guid) ? guid : null;
            if (underlyingType == typeof(DateTime)) return DateTime.TryParse(value, out var dateTime) ? dateTime : null;
            if (underlyingType == typeof(DateTimeOffset))
                return DateTimeOffset.TryParse(value, out var dateTimeOffset) ? dateTimeOffset : null;
            if (underlyingType == typeof(DateOnly)) return DateOnly.TryParse(value, out var dateOnly) ? dateOnly : null;
            if (underlyingType == typeof(TimeOnly)) return TimeOnly.TryParse(value, out var timeOnly) ? timeOnly : null;
            if (underlyingType.IsEnum)
                return Enum.TryParse(underlyingType, value, true, out var enumValue) ? enumValue : null;

            var converter = TypeDescriptor.GetConverter(underlyingType);
            if (converter.CanConvertFrom(typeof(string))) return converter.ConvertFromInvariantString(value);

            return Convert.ChangeType(value, underlyingType);
        }
        catch
        {
            return null;
        }
    }
}