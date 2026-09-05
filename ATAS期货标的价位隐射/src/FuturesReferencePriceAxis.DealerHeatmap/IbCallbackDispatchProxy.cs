namespace WolfMoss.ATAS.PriceMapping;

using System.Reflection;

/// <summary>
/// Public, non-sealed proxy required by <see cref="DispatchProxy"/> when the
/// official IB API EWrapper interface is supplied at runtime.
/// </summary>
public class IbCallbackDispatchProxy : DispatchProxy
{
    public Action<MethodInfo, object?[]>? Handler { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod != null)
            Handler?.Invoke(targetMethod, args ?? Array.Empty<object?>());

        if (targetMethod?.ReturnType == typeof(void) || targetMethod == null)
            return null;

        return targetMethod.ReturnType.IsValueType
            ? Activator.CreateInstance(targetMethod.ReturnType)
            : null;
    }
}
