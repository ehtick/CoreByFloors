using Elements;
using Elements.Geometry;
using Elements.Geometry.Solids;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreByFloors
{
    public static class CoreByFloors
    {
        /// <summary>
        /// The CoreByFloors function.
        /// </summary>
        /// <param name="model">The input model.</param>
        /// <param name="input">The arguments to the execution.</param>
        /// <returns>A CoreByFloorsOutputs instance containing computed results and the model with any new elements.</returns>
        public static CoreByFloorsOutputs Execute(Dictionary<string, Model> inputModels, CoreByFloorsInputs input)
        {
            var output = new CoreByFloorsOutputs();
            var floors = inputModels["Floors"].AllElementsOfType<Floor>();
            if (floors.Count() < 1)
            {
                output.Warnings.Add("No Floors found in model.");
                return output;
            }
            var floorsOrdered = floors.OrderBy(f => f.Elevation);
            var projectedUnion = Profile.UnionAll(floors.Select(f => f.Profile));
            // output.Model.AddElements(projectedUnion.Select(p => new Floor(p, 0.1, null, BuiltInMaterials.XAxis)));
            // return output;

            foreach (var profileGroup in projectedUnion)
            {
                var floorsInUnion = floorsOrdered.Where(f => profileGroup.Contains(PointInternal(f.Profile.Perimeter)));
                var maxHeight = floorsInUnion.Last().Elevation + 1;
                var minHeight = floorsInUnion.First().Elevation;
                if (floorsInUnion.Count() == 1)
                {
                    maxHeight += 3;
                }
                if (input.Overrides != null)
                {
                    var overrides = input.Overrides.Cores;
                    var coreInThisGroup = overrides.FirstOrDefault(c => profileGroup.Contains(c.Identity.Centroid));
                    if (coreInThisGroup != null)
                    {
                        var boundary = coreInThisGroup.Value.Profile.Perimeter;
                        var rep = new Representation(new[] { new Extrude(boundary, maxHeight - minHeight, Vector3.ZAxis, false) });
                        var overrideCore = new Elements.ServiceCore(boundary, 0, maxHeight - minHeight, coreInThisGroup.Identity.Centroid, new Transform(0, 0, minHeight), BuiltInMaterials.Concrete, rep, false, Guid.NewGuid(), null);
                        output.Model.AddElement(overrideCore);
                        continue;
                    }
                }
                var runningProfile = new List<Profile> { profileGroup };
                foreach (var floor in floorsInUnion)
                {
                    runningProfile = Profile.Intersection(runningProfile, new[] { floor.Profile });
                }
                if (runningProfile.Count == 0)
                {
                    Console.WriteLine("😪 there's nobody here!");
                    continue;
                }
                var mainProfile = runningProfile.OrderBy(p => p.Area()).Last();
                var coreCenterPt = BestCoreLocation(mainProfile.Perimeter, out var dominantAngle);
                var perpAngle = dominantAngle.Cross(Vector3.ZAxis);
                var rect = new Polygon(new[]  {
                  coreCenterPt + dominantAngle * input.Length / 2 + perpAngle * input.Width / 2,
                  coreCenterPt - dominantAngle * input.Length / 2 + perpAngle * input.Width / 2,
                  coreCenterPt - dominantAngle * input.Length / 2 - perpAngle * input.Width / 2,
                  coreCenterPt + dominantAngle * input.Length / 2 - perpAngle * input.Width / 2,
                  });
                var representation = new Representation(new[] { new Extrude(rect, maxHeight - minHeight, Vector3.ZAxis, false) });
                var core = new Elements.ServiceCore(rect, 0, maxHeight - minHeight, coreCenterPt, new Transform(0, 0, minHeight), BuiltInMaterials.Concrete, representation, false, Guid.NewGuid(), null);
                output.Model.AddElement(core);
            }

            return output;
        }

        private static Vector3 GetDominantAxis(IEnumerable<Line> allLines, Model model = null)
        {
            var refVec = new Vector3(1, 0, 0);
            var lengthByAngle = new Dictionary<double, (double length, IEnumerable<double> angles)>();
            var matsByAngle = new Dictionary<double, Material>();
            foreach (var line in allLines)
            {
                var wallDir = line.Direction();
                var trueAngle = refVec.PlaneAngleTo(wallDir) % 180;
                var angle = Math.Round(trueAngle);
                if (!lengthByAngle.ContainsKey(angle))
                {
                    lengthByAngle[angle] = (line.Length(), new[] { trueAngle });
                    matsByAngle[angle] = new Material(angle.ToString(), HlsToColor(angle, 0.5, 1));
                    model?.AddElement(new ModelCurve(line, matsByAngle[angle]));
                }
                else
                {
                    var existingRecord = lengthByAngle[angle];
                    lengthByAngle[angle] = (existingRecord.length + line.Length(), existingRecord.angles.Union(new[] { trueAngle }));
                    model?.AddElement(new ModelCurve(line, matsByAngle[angle]));
                }
            }
            var dominantAngle = lengthByAngle.ToArray().OrderByDescending(kvp => kvp.Value).First().Value.angles.Average();
            var rotation = new Transform();
            rotation.Rotate(dominantAngle);
            return rotation.OfVector(refVec);
        }

        public static Color HlsToColor(double h, double l, double s)
        {
            double p2;
            if (l <= 0.5) p2 = l * (1 + s);
            else p2 = l + s - l * s;

            double p1 = 2 * l - p2;
            double double_r, double_g, double_b;
            if (s == 0)
            {
                double_r = l;
                double_g = l;
                double_b = l;
            }
            else
            {
                double_r = QqhToRgb(p1, p2, h + 120);
                double_g = QqhToRgb(p1, p2, h);
                double_b = QqhToRgb(p1, p2, h - 120);
            }

            return new Color(double_r, double_g, double_b, 1);
        }

        private static double QqhToRgb(double q1, double q2, double hue)
        {
            if (hue > 360) hue -= 360;
            else if (hue < 0) hue += 360;

            if (hue < 60) return q1 + (q2 - q1) * hue / 60;
            if (hue < 180) return q2;
            if (hue < 240) return q1 + (q2 - q1) * (240 - hue) / 60;
            return q1;
        }

        private static Vector3 BestCoreLocation(Polygon p, out Vector3 dominantAngle, Model model = null)
        {
            var candidateShape = p;
            int maxIterationCount = 1000;
            int iterationCount = 0;
            while (true)
            {
                if (iterationCount > maxIterationCount)
                {
                    dominantAngle = GetDominantAxis(p.Segments(), model);
                    return p.Centroid();
                }
                // model?.AddElement(p);
                var shrink = p.Offset(-1)?.OrderBy(p => p.Area()).LastOrDefault();

                if (shrink != null)
                {
                    p = shrink;
                }
                else
                {
                    dominantAngle = GetDominantAxis(p.Segments(), model);
                    return p.Centroid();
                }
                iterationCount++;
            }
        }

        private static Vector3 PointInternal(Polygon p, bool closestToCenter = false, Model model = null)
        {
            var centroid = p.Centroid();
            Vector3? returnCandidate = null;
            if (p.Contains(centroid))
            {
                return centroid;
            }
            int currentIndex = 0;
            while (true)
            {
                if (currentIndex == p.Vertices.Count)
                {
                    return returnCandidate ?? centroid;
                }
                var a = p.Vertices[currentIndex];
                var b = p.Vertices[(currentIndex + 2) % p.Vertices.Count];
                model?.AddElement(new Line(a, b));
                var candidate = (a + b) * 0.5;
                model?.AddElement(new Circle(candidate, 1));
                if (p.Contains(candidate))
                {
                    if (closestToCenter)
                    {
                        if (returnCandidate == null || returnCandidate?.DistanceTo(centroid) > candidate.DistanceTo(centroid))
                        {
                            returnCandidate = candidate;
                        }
                    }
                    else
                    {
                        return candidate;
                    }
                }
                currentIndex++;
            }
        }

    }
}