using Elements;
using System.Collections.Generic;
using System;
using System.Linq;

namespace CoreByFloors
{
	/// <summary>
	/// Override metadata for CoresOverrideRemoval
	/// </summary>
	public partial class CoresOverrideRemoval : IOverride
	{
        public static string Name = "Cores Removal";
        public static string Dependency = null;
        public static string Context = "[*discriminator=Elements.ServiceCore]";
		public static string Paradigm = "Edit";

        /// <summary>
        /// Get the override name for this override.
        /// </summary>
        public string GetName() {
			return Name;
		}

		public object GetIdentity() {

			return Identity;
		}

	}

}