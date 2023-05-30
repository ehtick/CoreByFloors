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
            inputModels.TryGetValue("Floors", out var floorsModel);
            var floors = (floorsModel ?? new Model()).AllElementsOfType<Floor>();

            var hasLevels = inputModels.TryGetValue("Levels", out var levelsModel);
            var levelVolumes = hasLevels ? levelsModel.AllElementsOfType<LevelVolume>().OrderBy(f => f.Transform.Origin.Z).ToList() : new List<LevelVolume>();
            if (inputModels.TryGetValue("Conceptual Mass", out var massModel))
            {
                levelVolumes.AddRange(massModel.AllElementsOfType<LevelVolume>());
                hasLevels = true;
            }
            if (!floors.Any() && levelVolumes.Count < 1)
            {
                output.Warnings.Add("No Floors or Levels found in model.");
                return output;
            }
            if (!floors.Any())
            {
                floors = levelVolumes.Select(lv => new Floor(lv.Profile, 0.01, lv.Transform));
            }
            var floorsOrdered = floors.OrderBy(f => f.Elevation);
            var projectedUnion = Profile.UnionAll(floors.Select(f => f.Profile.Transformed(f.Transform)));
            var allProfilesAtElevation = floors.Select(f => f.Profile.Transformed(f.Transform)).Union(levelVolumes.Select(lv => lv.Profile.Transformed(lv.Transform.Concatenated(new Transform(0, 0, lv.Height)))));
            foreach (var profileGroup in projectedUnion)
            {
                List<LevelVolume> levelsInUnion = new();
                var floorsInUnion = floorsOrdered.Where(f => profileGroup.Contains(PointInternal(f.Profile.Perimeter)));
                var maxHeight = floorsInUnion.Last().Elevation + 1;
                var minHeight = floorsInUnion.First().Elevation;

                if (hasLevels)
                {
                    levelsInUnion.AddRange(levelVolumes.Where(v => profileGroup.Contains(PointInternal(v.Profile.Perimeter))));
                    var last = levelsInUnion.LastOrDefault();
                    if (last != null)
                    {
                        var levelmaxHeight = last.Height + last.Transform.Origin.Z + 1;
                        maxHeight = Math.Max(levelmaxHeight, maxHeight);
                    }
                }
                else
                {
                    foreach (var floor in floorsInUnion)
                    {
                        levelsInUnion.Add(new LevelVolume()
                        {
                            Profile = floor.Profile,
                            Area = floor.Area(),
                            Height = 3,
                            BuildingName = "Unknown",
                            Transform = floor.Transform,
                            Name = floor.Name
                        });
                    }
                }
                if (floorsInUnion.Count() == 1 && !hasLevels)
                {
                    // if we only have a single floor, we don't want a flat little core.
                    maxHeight += 3;
                }

                var allCores = new List<ServiceCore>();

                var runningProfile = new List<Profile> { profileGroup };
                foreach (var floor in floorsInUnion)
                {
                    runningProfile = Profile.Intersection(runningProfile, new[] { floor.Profile });
                }
                var hasValidAutoLocation = true;
                if (runningProfile.Count == 0)
                {
                    Console.WriteLine("😪 there's nobody here!");
                    hasValidAutoLocation = false;
                }
                Vector3 dominantAngle = (1, 0, 0);
                if (hasValidAutoLocation)
                {
                    var mainProfile = runningProfile.OrderBy(p => p.Area()).Last();
                    var coreCenterPt = BestCoreLocation(mainProfile.Perimeter, out dominantAngle);
                    var (length, depth) = GenerateCoreDimensions(mainProfile.Perimeter, coreCenterPt, dominantAngle);
                    allCores.Add(GenerateCoreAtPoint(length, depth, maxHeight, minHeight, allProfilesAtElevation, coreCenterPt, dominantAngle));
                }
                else
                {
                    var coreCenterPt = BestCoreLocation(profileGroup.Perimeter, out dominantAngle);
                    var (length, depth) = GenerateCoreDimensions(profileGroup.Perimeter, coreCenterPt, dominantAngle);
                    allCores.Add(GenerateCoreAtPoint(length, depth, maxHeight, minHeight, allProfilesAtElevation, coreCenterPt, dominantAngle));
                }

                // Deprecated old pathway
                foreach (var addlCore in input.AdditionalCoreLocations ?? new List<Vector3>())
                {
                    if (profileGroup.Contains(addlCore))
                    {
                        allCores.Add(GenerateCoreAtPoint(input.Length, input.Width, maxHeight, minHeight, allProfilesAtElevation, addlCore, dominantAngle));
                    }
                }

                if (input.Overrides?.Additions?.Cores != null)
                {
                    foreach (var coreOverride in input.Overrides.Additions.Cores)
                    {
                        var coreLocation = coreOverride.Value.Profile.Centroid();
                        if (profileGroup.Contains(coreLocation))
                        {
                            var core = GenerateCoreFromPolygon(coreOverride.Value.Profile, input, maxHeight, minHeight, allProfilesAtElevation);
                            core.BoundaryIsUnedited = ServiceCore.IsRectangle(coreOverride.Value.Profile, out var length, out var depth);
                            core.Length = length;
                            core.Depth = depth;
                            Identity.AddOverrideIdentity(core, coreOverride);
                            allCores.Add(core);
                        }
                    }
                }

                if (allCores.Count == 0)
                {
                    output.Warnings.Add("Unable to automatically place core for at least one building. Use Additional Core Locations to specify a core position manually.");
                }
                if (input.Overrides?.CoreDimensions != null)
                {
                    var overrides = input.Overrides.CoreDimensions;
                    var coresInThisGroup = overrides.Where(c => profileGroup.Contains(c.Identity.Centroid));
                    if (coresInThisGroup != null && coresInThisGroup.Any())
                    {
                        foreach (var coreInThisGroup in coresInThisGroup)
                        {
                            var matchingExistingCore = allCores.OrderBy(c => c.Centroid.DistanceTo(coreInThisGroup.Identity.Centroid)).First();
                            var coreCenterPt = matchingExistingCore.Centroid;
                            var dominantAngleDrawn = matchingExistingCore.Profile.Perimeter.Segments().OrderBy(s => s.Length()).Last().Direction();
                            var newCore = GenerateCoreAtPoint(coreInThisGroup.Value.Length, coreInThisGroup.Value.Depth, maxHeight, minHeight, allProfilesAtElevation, coreCenterPt, dominantAngleDrawn);
                            newCore.Length = coreInThisGroup.Value.Length;
                            newCore.Depth = coreInThisGroup.Value.Depth;
                            Identity.AddOverrideIdentity(newCore, coreInThisGroup);
                            allCores.Add(newCore);
                            allCores.Remove(matchingExistingCore);
                        }
                    }
                }

                if (input.Overrides?.Cores != null)
                {
                    var overrides = input.Overrides.Cores;
                    var coresInThisGroup = overrides.Where(c => profileGroup.Contains(c.Identity.Centroid));
                    if (coresInThisGroup != null && coresInThisGroup.Count() > 0)
                    {
                        foreach (var coreInThisGroup in coresInThisGroup)
                        {
                            var matchingExistingCore = allCores.OrderBy(c => c.Centroid.DistanceTo(coreInThisGroup.Identity.Centroid)).First();

                            var boundary = coreInThisGroup.Value.Profile.Perimeter;
                            var rep = new Representation(new[] { new Extrude(boundary, matchingExistingCore.Height, Vector3.ZAxis, false) });
                            var overrideCore = new Elements.ServiceCore(boundary, 0, matchingExistingCore.Height, coreInThisGroup.Identity.Centroid, new Transform(0, 0, minHeight), BuiltInMaterials.Concrete, rep, false, Guid.NewGuid(), null);
                            Identity.AddOverrideIdentity(overrideCore, "Cores", coreInThisGroup.Id, coreInThisGroup.Identity);
                            allCores.Remove(matchingExistingCore);
                            overrideCore.BoundaryIsUnedited = false;
                            allCores.Add(overrideCore);
                        }
                    }
                    // var coreInThisGroup = overrides.FirstOrDefault(c => profileGroup.Contains(c.Identity.Centroid));
                    // if (coreInThisGroup != null)
                    // {
                    //     var boundary = coreInThisGroup.Value.Profile.Perimeter;
                    //     var rep = new Representation(new[] { new Extrude(boundary, maxHeight - minHeight, Vector3.ZAxis, false) });
                    //     var overrideCore = new Elements.ServiceCore(boundary, 0, maxHeight - minHeight, coreInThisGroup.Identity.Centroid, new Transform(0, 0, minHeight), BuiltInMaterials.Concrete, rep, false, Guid.NewGuid(), null);
                    //     output.Model.AddElement(overrideCore);
                    //     continue;
                    // }
                }

                if (input.Overrides?.Removals != null)
                {
                    foreach (var coreRemoval in input.Overrides?.Removals?.Cores)
                    {
                        var coreToRemove = allCores.OrderBy(c => c.Centroid.DistanceTo(coreRemoval.Identity.Centroid)).FirstOrDefault(c => c.Centroid.DistanceTo(coreRemoval.Identity.Centroid) < 1);
                        allCores.Remove(coreToRemove);

                    }
                }
                foreach (var level in levelsInUnion)
                {
                    foreach (var core in allCores)
                    {
                        if (level.Transform.Origin.Z <= core.Height)
                        {
                            var sb = new CoreArea()
                            {
                                Boundary = core.Profile,
                                Area = Math.Abs(core.Profile.Area()),
                                // this silly 1 meter extrusion keeps us from seeing the through the core subsection in a plan view. Not a good long-term solution.
                                Representation = new Extrude(core.Profile, 1, Vector3.ZAxis, false),
                                Transform = level.Transform,
                                Material = BuiltInMaterials.Concrete,
                                Core = core.Id,
                            };
                            sb.AdditionalProperties["Building Name"] = level.BuildingName;
                            sb.AdditionalProperties["Level Name"] = level.Name;
                            var coreLines = new CoreLines(core.Profile, level.Transform);
                            output.Model.AddElements(sb, coreLines);
                        }
                    }
                }
                output.Model.AddElements(allCores);
            }

            return output;
        }

        private static (double length, double depth) GenerateCoreDimensions(Polygon perimeter, Vector3 coreCenterPt, Vector3 dominantAngle)
        {
            var lengthLine = new Line(coreCenterPt - dominantAngle * 0.1, coreCenterPt + dominantAngle * 0.1);
            lengthLine = lengthLine.ExtendTo(perimeter);
            var perpAngle = dominantAngle.Cross(Vector3.ZAxis).Unitized();
            var depthLine = new Line(coreCenterPt - perpAngle * 0.1, coreCenterPt + perpAngle * 0.1);
            depthLine = depthLine.ExtendTo(perimeter);
            var defaultLength = 18.0;
            var defaultDepth = 10.0;
            var minLeaseDepthLength = 9.5;
            var minLeaseDepthDepth = 7.5;
            var minLength = 10.0;
            var minDepth = 7.5;
            var length = Math.Max(minLength, Math.Min(defaultLength, lengthLine.Length() - minLeaseDepthLength - minLeaseDepthLength));
            var depth = Math.Max(minDepth, Math.Min(defaultDepth, depthLine.Length() - minLeaseDepthDepth - minLeaseDepthDepth));
            return (length, depth);
        }

        private static ServiceCore GenerateCoreAtPoint(double length, double depth, double maxHeight, double minHeight, IEnumerable<Profile> profiles, Vector3 coreCenterPt, Vector3 dominantAngle)
        {
            var profilesContainingPoint = profiles.Where(p => p.Contains(coreCenterPt));
            if (profilesContainingPoint.Any())
            {
                var elevationsIncluded = profilesContainingPoint.Select(p => p.Perimeter.Vertices.First().Z).OrderBy(z => z);
                maxHeight = profilesContainingPoint.Select(p => p.Perimeter.Vertices.First().Z).OrderBy(z => z).Last() + 1;
            }

            var perpAngle = dominantAngle.Cross(Vector3.ZAxis);
            var rect = new Polygon(new[]  {
                  coreCenterPt + dominantAngle * length / 2 + perpAngle * depth / 2,
                  coreCenterPt - dominantAngle * length / 2 + perpAngle * depth / 2,
                  coreCenterPt - dominantAngle * length / 2 - perpAngle * depth / 2,
                  coreCenterPt + dominantAngle * length / 2 - perpAngle * depth / 2,
                  });
            var representation = new Representation(new[] { new Extrude(rect, maxHeight - minHeight, Vector3.ZAxis, false) });
            var core = new Elements.ServiceCore(rect, 0, maxHeight - minHeight, coreCenterPt, new Transform(0, 0, minHeight), BuiltInMaterials.Concrete, representation, false, Guid.NewGuid(), null)
            {
                Length = length,
                Depth = depth
            };
            return core;
        }

        private static ServiceCore GenerateCoreFromPolygon(Polygon rect, CoreByFloorsInputs input, double maxHeight, double minHeight, IEnumerable<Profile> profiles)
        {
            var coreCenterPt = rect.Centroid();
            var profilesContainingPoint = profiles.Where(p => p.Contains(coreCenterPt));
            if (profilesContainingPoint.Any())
            {
                var elevationsIncluded = profilesContainingPoint.Select(p => p.Perimeter.Vertices.First().Z).OrderBy(z => z);
                maxHeight = profilesContainingPoint.Select(p => p.Perimeter.Vertices.First().Z).OrderBy(z => z).Last() + 1;
            }
            var representation = new Representation(new[] { new Extrude(rect, maxHeight - minHeight, Vector3.ZAxis, false) });
            var core = new Elements.ServiceCore(rect, 0, maxHeight - minHeight, coreCenterPt, new Transform(0, 0, minHeight), BuiltInMaterials.Concrete, representation, false, Guid.NewGuid(), null);
            return core;
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
                    var (length, angles) = lengthByAngle[angle];
                    lengthByAngle[angle] = (length + line.Length(), angles.Union(new[] { trueAngle }));
                    model?.AddElement(new ModelCurve(line, matsByAngle[angle]));
                }
            }
            var dominantAngle = lengthByAngle.ToArray().OrderByDescending(kvp => kvp.Value.length).First().Value.angles.Average();
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
                Polygon shrink = null;
                try
                {
                    shrink = p.Offset(-1)?.OrderBy(p => p.Area()).LastOrDefault();
                }
                catch
                {

                }

                if (shrink != null)
                {
                    p = shrink;
                }
                else
                {
                    dominantAngle = GetDominantAxis(p.Segments(), model);
                    return p.PointInternal();
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