namespace TarkovHelper.Services.Map;

/// <summary>
/// Keeps quest-marker label grouping tied to the actual marker anchors. Long labels
/// and transitive label overlaps must not merge objectives from unrelated locations.
/// </summary>
internal static class MapMarkerGroupingPolicy
{
    internal const double MaximumAnchorDistance = 48.0;

    internal static bool AreAnchorsClose(
        double firstX,
        double firstY,
        double secondX,
        double secondY)
    {
        var deltaX = firstX - secondX;
        var deltaY = firstY - secondY;
        return (deltaX * deltaX) + (deltaY * deltaY) <=
               MaximumAnchorDistance * MaximumAnchorDistance;
    }

    internal static bool CanJoinCluster<T>(
        IReadOnlyCollection<T> cluster,
        T candidate,
        Func<T, (double X, double Y)> positionSelector)
    {
        var candidatePosition = positionSelector(candidate);
        return cluster.All(existing =>
        {
            var existingPosition = positionSelector(existing);
            return AreAnchorsClose(
                existingPosition.X,
                existingPosition.Y,
                candidatePosition.X,
                candidatePosition.Y);
        });
    }

    internal static List<List<T>> PartitionByAnchorProximity<T>(
        IEnumerable<T> candidates,
        Func<T, (double X, double Y)> positionSelector)
    {
        var clusters = new List<List<T>>();
        foreach (var candidate in candidates)
        {
            var cluster = clusters.FirstOrDefault(existing =>
                CanJoinCluster(existing, candidate, positionSelector));

            if (cluster == null)
            {
                clusters.Add(new List<T> { candidate });
            }
            else
            {
                cluster.Add(candidate);
            }
        }

        return clusters;
    }
}
