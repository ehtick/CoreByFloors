using System;
using System.Linq;
using Elements.Geometry;

namespace Elements
{
    public partial class ServiceCore : GeometricElement
    {
        public bool BoundaryIsUnedited { get; set; } = true;
        public double Length { get; set; } = 0;
        public double Depth { get; set; } = 0;

        public static bool IsRectangle(Profile profile)
        {
            return IsRectangle(profile, out _, out _);
        }
        public static bool IsRectangle(Profile profile, out double length, out double depth)
        {
            length = 0;
            depth = 0;
            var segments = profile.Perimeter.Segments();
            if (segments.Length != 4)
            {
                return false;
            }
            var primaryDir = segments[0].Direction();
            var secondaryDir = segments[1].Direction();
            var dimsSegments = new[] { segments[0].Length(), segments[1].Length() }.OrderBy(x => x).ToArray();
            length = dimsSegments[1];
            depth = dimsSegments[0];
            if (Math.Abs(primaryDir.Dot(secondaryDir)) > 0.01)
            {
                return false;
            }
            if (!segments[0].Length().ApproximatelyEquals(segments[2].Length()))
            {
                return false;
            }
            if (!segments[1].Length().ApproximatelyEquals(segments[3].Length()))
            {
                return false;
            }
            return true;
        }
    }
}