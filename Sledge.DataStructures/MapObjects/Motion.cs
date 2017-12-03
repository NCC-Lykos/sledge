using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sledge.DataStructures.MapObjects
{
    public class Motion
    {
       /*public string Name { get; set; }
        public int ID { get; set; }
        public double CurrentKeyTime { get; set; }
        public string Transform { get; set; }
        public bool IsMotion { get; set; }
        public GEDotNet.GEMotion NativeMotion { get; set; }*/

        /// <summary>
        /// We're reading the raw model lines into this for now, instead of 
        /// parsing them out into anything, and then just writing them back
        /// to the file as-is. Eventually we can parse the motion/model contents.
        /// </summary>
        public List<string> RawModelLines = new List<string>();
    }
}
