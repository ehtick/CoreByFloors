using Elements;
using System;
using System.Linq;
using System.Collections.Generic;
using Elements.Geometry;
namespace Elements
{
    public partial class CoreLines : ModelLines
    {
        public CoreLines(Profile profile, Transform transform)
        {
            Lines = profile.Segments();
            Transform = transform.Concatenated(new Transform(0, 0, 0.001));
            Material = new Material("CoreLines", Colors.Black)
            {
                EdgeDisplaySettings = new EdgeDisplaySettings
                {
                    WidthMode = EdgeDisplayWidthMode.ScreenUnits,
                    LineWidth = 3.0,
                }
            };
            SetSelectable(false);
        }
    }
}