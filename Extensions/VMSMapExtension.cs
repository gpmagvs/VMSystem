namespace VMSystem.Extensions
{
    public static class VMSMapExtension
    {
        public static string GetDisplayAtCurrentMap(this int tagNumber)
        {
            return StaMap.GetPointByTagNumber(tagNumber)?.Graph.Display ?? string.Empty;
        }
    }
}
