using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using Sledge.DataStructures.MapObjects;
using System.Globalization;
using System.IO;
using Sledge.Common;
using System.Diagnostics;
using Sledge.DataStructures.Geometric;
using Sledge.Providers;

namespace Sledge.Providers.Map
{
    public class ThreeDtProvider : MapProvider
    {
        protected override IEnumerable<MapFeature> GetFormatFeatures()
        {
            return new[]
            {
                MapFeature.Worldspawn,
                MapFeature.Solids,
                MapFeature.Entities,

                MapFeature.Motions,

                MapFeature.Colours,
                MapFeature.SingleVisgroups,
            };
        }

        private float MapVersion { get; set; }

        protected override bool IsValidForFileName(string filename)
        {
            return filename.EndsWith(".3dt", true, CultureInfo.InvariantCulture);
        }

        private void Assert(bool b, string message = "Malformed file")
        {
            if (!b) throw new Exception(message);
        }

        private string FormatCoordinate(Coordinate c)
        {
            return c.X.ToString("0.000000", CultureInfo.InvariantCulture)
                    + " " + c.Z.ToString("0.000000", CultureInfo.InvariantCulture)
                    + " " + (-c.Y).ToString("0.000000", CultureInfo.InvariantCulture);
        }

        private string FormatCoordinateNonSwitch(Coordinate c)
        {
            return c.X.ToString("0.000000", CultureInfo.InvariantCulture)
                    + " " + c.Y.ToString("0.000000", CultureInfo.InvariantCulture)
                    + " " + c.Z.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        private string FormatIntCoordinate(Coordinate c)
        {
            return c.X.ToString("0", CultureInfo.InvariantCulture)
                    + " " + c.Z.ToString("0", CultureInfo.InvariantCulture)
                    + " " + (-c.Y).ToString("0", CultureInfo.InvariantCulture);
        }

        private string FormatColor(System.Drawing.Color c)
        {
            return c.R.ToString("0", CultureInfo.InvariantCulture)
                   + " " + c.G.ToString("0", CultureInfo.InvariantCulture)
                   + " " + c.B.ToString("0", CultureInfo.InvariantCulture);
        }

        private static List<string> FaceProperties = new List<string> { "NumPoints", "Flags", "Light", "MipMapBias", "Translucency", "Reflectivity" };
        private Face ReadFace(Solid parent, StreamReader rdr, IDGenerator generator)
        {
            const NumberStyles ns = NumberStyles.Float;
            var properties = new Dictionary<string, string>();

            foreach (var prop in FaceProperties)
            {
                (string name, string value) = ReadProperty(rdr);
                Assert(name == prop);
               
                properties[name] = value;
            }

            var numPoints = int.Parse(properties["NumPoints"]);
            var coords = new List<Coordinate>(numPoints);
            for (int i = 0; i < numPoints; ++i)
            {
                var line = rdr.ReadLine().Trim();
                Assert(line.StartsWith("Vec3d"));

                var split = line.Split(' ');
                Assert(split.Length == 4);

                var coord = Coordinate.Parse(split[1], split[2], split[3]);
                var tmp = coord.Y;
                coord.Y = -coord.Z;
                coord.Z = tmp;

                coords.Add(coord);
            }
            var poly = new Polygon(coords);

            // Parse texture
            var texSplit = rdr.ReadLine().Trim().Split(' ');
            Assert(texSplit.Length == 11);

            var face = new Face(generator.GetNextFaceID())
            {
                Plane = poly.GetPlane(),
                Parent = parent,
                Texture = { Name = texSplit[10].Trim('"') },
                Flags = (FaceFlags)int.Parse(properties["Flags"]),
                Translucency = float.Parse(properties["Translucency"]),
                Light = int.Parse(properties["Light"]),
            };
            face.Vertices.AddRange(poly.Vertices.Select(x => new Vertex(x, face)));

            NCAlignTextureToWorld(face);

            face.Texture.XShift = decimal.Parse(texSplit[4], ns, CultureInfo.InvariantCulture);
            face.Texture.YShift = decimal.Parse(texSplit[5], ns, CultureInfo.InvariantCulture);
            face.SetTextureRotation(decimal.Parse(texSplit[2], ns, CultureInfo.InvariantCulture));
            face.Texture.XScale = decimal.Parse(texSplit[7], ns, CultureInfo.InvariantCulture);
            face.Texture.YScale = decimal.Parse(texSplit[8], ns, CultureInfo.InvariantCulture);

            face.UpdateBoundingBox();

            // Parse light scale
            var ln = rdr.ReadLine();
            var scale = ln.Split(' ');
            face.LightScale = Coordinate.Parse(scale[1], scale[2], "0");

            // Skip currently unknown values Transform and Pos
            if (MapVersion > 1.31)
            {
                rdr.ReadLine(); rdr.ReadLine();
            }

            return face;
        }

        private static void NCAlignTextureToWorld(Face face)
        {
            var direction = NCClosestAxisToNormal(face.Plane);
            face.Texture.UAxis = direction == Coordinate.UnitX ? Coordinate.UnitY : Coordinate.UnitX;
            face.Texture.VAxis = direction == Coordinate.UnitZ ? -Coordinate.UnitY : -Coordinate.UnitZ;
        }

        private static Coordinate NCClosestAxisToNormal(Plane plane)
        {
            var norm = plane.Normal.Absolute();

            if (norm.Z >= norm.X && norm.Z >= norm.Y) return Coordinate.UnitZ;
            if (norm.X >= norm.Y) return Coordinate.UnitX;
            return Coordinate.UnitY;
        }

        private void WriteFace(Face face, StreamWriter wr)
        {
            var flags = face.Flags == 0 ? "512" : ((int)face.Flags).ToString();
            WriteProperty("NumPoints", face.Vertices.Count().ToString(), wr, false, 2);
            WriteProperty("Flags", flags, wr, false, 2);
            WriteProperty("Light", face.Light.ToString(), wr, false, 2);
            WriteProperty("MipMapBias", "1.000000", wr, false, 2);
            WriteProperty("Translucency", face.Translucency.ToString(), wr, false, 2);
            WriteProperty("Reflectivity", "1.000000", wr, false, 2);

            foreach (var vert in face.Vertices)
            {
                WriteProperty("Vec3d", FormatCoordinate(vert.Location), wr, false, 3);
            }

            var texInfo = string.Format("Rotate {0} Shift {1} {2} Scale {3} {4} Name \"{5}\"",
                face.Texture.Rotation.ToString("0.000000", CultureInfo.InvariantCulture),
                face.Texture.XShift.ToString("0", CultureInfo.InvariantCulture),
                face.Texture.YShift.ToString("0", CultureInfo.InvariantCulture),
                face.Texture.XScale.ToString("0.000000", CultureInfo.InvariantCulture),
                face.Texture.YScale.ToString("0.000000", CultureInfo.InvariantCulture),
                face.Texture.Name);
            WriteProperty("TexInfo", texInfo, wr, false, 3);

            if (face.LightScale != null)
                WriteProperty("LightScale",
                face.LightScale.X.ToString("0.000000", CultureInfo.InvariantCulture) + " "
                + face.LightScale.Y.ToString("0.000000", CultureInfo.InvariantCulture), wr, false, 2);
            else
                WriteProperty("LightScale", "1.000000 1.000000", wr, false, 2);


            /*var fnorm = face.Plane.Normal;
            if (Math.Abs(fnorm.X) > Math.Abs(fnorm.Z))
            {
                if (Math.Abs(fnorm.X) > Math.Abs(fnorm.Y))
                {
                    if (fnorm.X > 0)
                    {
                        fnorm.X = -fnorm.X;
                        fnorm.Y = -fnorm.Y;
                        fnorm.Z = -fnorm.Z;
                    }
                }
                else
                {
                    if (fnorm.Y > 0)
                    {
                        fnorm.X = -fnorm.X;
                        fnorm.Y = -fnorm.Y;
                        fnorm.Z = -fnorm.Z;
                    }
                }
            }
            else
            {
                if (Math.Abs(fnorm.Z) > Math.Abs(fnorm.Y))
                {
                    if (fnorm.Y > 0)
                    {
                        fnorm.X = -fnorm.X;
                        fnorm.Y = -fnorm.Y;
                        fnorm.Z = -fnorm.Z;
                    }
                }
                else
                {
                    if (fnorm.Z > 0)
                    {
                        fnorm.X = -fnorm.X;
                        fnorm.Y = -fnorm.Y;
                        fnorm.Z = -fnorm.Z;
                    }
                }
            }

            var dest = Coordinate.Zero;
            var axis = dest.Cross(fnorm);
            var cosv = Math.Min((double)dest.Dot(fnorm), 1.0d);
            var theta = (decimal)Math.Acos(cosv);

            var tmp = axis.Y;
            axis.Y = -axis.Z;
            axis.Z = tmp;

            axis = axis.Normalise();
            var mat = axis == Coordinate.Zero ? Matrix.RotationX(theta) : Quaternion.AxisAngle(axis, theta).GetMatrix();
            var value = string.Format("{0} {1} {2} {3}", FormatCoordinateNonSwitch(mat.X), FormatCoordinateNonSwitch(mat.Y), FormatCoordinateNonSwitch(mat.Z), FormatCoordinateNonSwitch(mat.Shift));
            wr.WriteLine("\tTransform\t{0}", value);

            WriteProperty("Pos", FormatCoordinate(face.BoundingBox.Center), wr, false, 1);*/
        }

        private static List<string> SolidProperties = new List<string>{"Flags", "ModelId", "GroupId", "HullSize", "Type", "BrushFaces" };
        private Solid ReadSolid(StreamReader rdr, IDGenerator generator, string brushName = "NoName")
        {
            var properties = new Dictionary<string, string>();

            foreach (var prop in SolidProperties)
            {
                (string name, string value) = ReadProperty(rdr);
                Assert(name == prop);
                properties[name] = value;
            }

            var numFaces = int.Parse(properties["BrushFaces"]);
            var faces = new List<Face>(numFaces);
            var ret = new Solid(generator.GetNextObjectID()) { ClassName = brushName };
            for (int i = 0; i < numFaces; ++i)
            {
                faces.Add(ReadFace(ret, rdr, generator));
            }

            ret.Faces.AddRange(faces);

            // Ensure all the faces point outwards
            var origin = ret.GetOrigin();
            foreach (var face in ret.Faces)
            {
                if (face.Plane.OnPlane(origin) >= 0) face.Flip();
            }

            ret.UpdateBoundingBox();

            //var ret = Solid.CreateFromIntersectingPlanes(faces.Select(x => x.Plane), generator);
            ret.Colour = GetGenesisBrushColor(int.Parse(properties["Flags"]));
            ret.MetaData.Set("Flags", properties["Flags"]);
            ret.MetaData.Set("ModelId", properties["ModelId"]);
            ret.MetaData.Set("HullSize", properties["HullSize"]);
            ret.MetaData.Set("Type", properties["Type"]);

            int group = int.Parse(properties["GroupId"]);
            if (group > 0)
                ret.Visgroups.Add(group);

            return ret;
        }

        private static System.Drawing.Color GetGenesisBrushColor(int Flags)
        {
            //Determine type of brush and color it
            if (Flags == 72)
                return Color.FromArgb(255, 186, 85, 211);
            return Colour.GetRandomBrushColour();
        }

        private static void ReadKeyValue(Entity ent, string line)
        {
            var split = line.Split(' ');
            var key = split[1].Trim();
            var value = string.Join(" ", split.Skip(3)).Trim('"');

            if (key == "classname")
                ent.ClassName = value;
            else if (key.Equals("origin", StringComparison.OrdinalIgnoreCase))
            {
                var osp = value.Split(' ');
                ent.Origin = Coordinate.Parse(osp[0], osp[1], osp[2]);
                var tmp = ent.Origin.Y;
                ent.Origin.Y = -ent.Origin.Z;
                ent.Origin.Z = tmp;
            }
            else if (key == "%name%")
            {
                ent.EntityData.Name = value;
                ent.EntityData.SetPropertyValue(key, value);
            }
            else
            {
                if (key == "color")
                {
                    var csp = value.Split(' ');
                    var r = int.Parse(csp[0]);
                    var g = int.Parse(csp[1]);
                    var b = int.Parse(csp[2]);

                    ent.Colour = System.Drawing.Color.FromArgb(r, g, b);
                }

                ent.EntityData.SetPropertyValue(key, value);
            }
        }

        private void WriteKeyValue(string key, string value, StreamWriter wr)
        {
            string line = string.Format("Key {0} Value \"{1}\"", key, value);
            wr.WriteLine(line);
        }

        private static (string name, string value) ReadProperty(StreamReader rdr)
        {
            var line = rdr.ReadLine();
            return ReadProperty(line);
        }

        private static (string name, string value) ReadProperty(string line)
        {
            var split = line.Split(' ');
            return (split[0].Trim(), string.Join(" ", split.Skip(1)).Trim('"'));
        }

        private static void WriteProperty(string name, string value, StreamWriter wr, bool quote = false, int numTabs = 0, bool newlineValue = false, string defaultValue = null)
        {

            var bld = new StringBuilder();

            for (int i = 0; i < numTabs; ++i)
                bld.Append('\t');

            if (quote)
                bld.AppendFormat("{0} \"{1}\"", name, value);
            else if (!newlineValue)
                bld.AppendFormat("{0} {1}", name, value);
            else // This is because we need to newline the Transform in motions.
                bld.AppendFormat("{0}\r{1}", name, value);

            wr.WriteLine(bld.ToString());
        }

        private Entity ReadWorldEntity(StreamReader rdr, IDGenerator generator)
        {
            var ent = new Entity(generator.GetNextObjectID()) { EntityData = new EntityData(), Colour = Colour.GetRandomBrushColour() };
            ent.EntityData.Name = "worldspawn";

            string line;
            while ((line = rdr.ReadLine()).StartsWith("Brush"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, "Brush \"(?<BrushName>.*)\"");
                string brushName = "NoName";
                if (match.Success)
                    brushName = match.Groups["BrushName"].Value;

                var s = ReadSolid(rdr, generator, brushName);
                if (s != null) s.SetParent(ent, false);
            }

            ent.UpdateBoundingBox();
            return ent;
        }

        private void WriteWorldEntity(List<Solid> solids, StreamWriter wr)
        {
            foreach(var solid in solids)
            {
                int vis = solid.Visgroups[0];
                var flags = solid.MetaData.Get<string>("Flags");
                flags = string.IsNullOrWhiteSpace(flags) ? "1" : flags;

                WriteProperty("Brush", solid.ClassName ?? "NoName", wr, true);
                WriteProperty("Flags", flags, wr, false, 1);
                WriteProperty("ModelId", solid.MetaData.Get<string>("ModelId") ?? "0", wr, false, 1);
                WriteProperty("GroupId", (vis < 0 ? 0 : vis).ToString(), wr, false, 1);
                WriteProperty("HullSize", solid.MetaData.Get<string>("HullSize") ?? "1.000000", wr, false, 1);
                WriteProperty("Type", solid.MetaData.Get<string>("Type") ?? "2", wr, false, 1);
                WriteProperty("BrushFaces", solid.Faces.Count().ToString(), wr, false, 1);
                foreach (var face in solid.Faces)
                    WriteFace(face, wr);
            }
        }

        private static List<string> EntityProperties = new List<string> { "eStyle", "eOrigin", "eFlags", "eGroup", "ePairCount" };
        private Entity ReadEntity(StreamReader rdr, IDGenerator generator)
        {
            var properties = new Dictionary<string, string>();
            var ent = new Entity(generator.GetNextObjectID()) { EntityData = new EntityData(), Colour = Colour.GetRandomBrushColour() };

            string line = rdr.ReadLine();
            Assert(line.Trim() == "CEntity");

            foreach (var prop in EntityProperties)
            {
                (string name, string value) = ReadProperty(rdr);
                Assert(name == prop);
                properties[name] = value;
            }
            ent.EntityData.Flags = int.Parse(properties["eFlags"]);

            var numPairs = int.Parse(properties["ePairCount"]);
            for (int i = 0; i < numPairs; ++i)
            {
                line = rdr.ReadLine();
                ReadKeyValue(ent, line);
            }

            // Swallow end
            line = rdr.ReadLine();
            Assert(line == "End CEntity");

            ent.UpdateBoundingBox();
            return ent;
        }

        private void WriteEntity(Entity entity, StreamWriter wr)
        {
            WriteProperty("CEntity", "", wr);
            WriteProperty("eStyle", "0", wr);
            WriteProperty("eOrigin", FormatIntCoordinate(entity.Origin) + " 0", wr);
            WriteProperty("eFlags", entity.EntityData.Flags.ToString(), wr);
            WriteProperty("eGroup", "0", wr);
            WriteProperty("ePairCount", (entity.EntityData.Properties.Count() + 2).ToString(), wr);

            WriteKeyValue("classname", entity.ClassName, wr);
            WriteKeyValue(
                            (
                                (
                                    entity.ClassName == "AmbientSound" || 
                                    entity.ClassName == "light" || 
                                    entity.ClassName == "StaticSound" || 
                                    entity.ClassName == "Corona" || 
                                    entity.ClassName == "DynamicLight" || 
                                    entity.ClassName == "directionallight" ||
                                    entity.ClassName == "spotlight"
                                ) 
                                ? "origin" : "Origin"
                            ), 
                            FormatIntCoordinate(entity.Origin), 
                            wr
                        );
            foreach(var prop in entity.EntityData.Properties)
            {
                WriteKeyValue(prop.Key, prop.Value, wr);
            }

            WriteProperty("End", "CEntity", wr);
        }

        private static List<string> EntityListProperties = new List<string> { "EntCount", "CurEnt" };
        private List<Entity> ReadAllEntities(StreamReader rdr, IDGenerator generator)
        {
            var properties = new Dictionary<string, string>();
            var list = new List<Entity>();

            list.Add(ReadWorldEntity(rdr, generator));

            foreach (var prop in EntityListProperties)
            {
                (string name, string value) = ReadProperty(rdr);
                Assert(name == prop);
                properties[name] = value;
            }

            var numEntites = int.Parse(properties["EntCount"]);
            for (int i = 0; i < numEntites; ++i)
            {
                Entity ent = ReadEntity(rdr, generator);
                if (ent != null)
                    list.Add(ent);
            }

            return list;
        }

        private Dictionary<string, string> ReadMapStats(StreamReader rdr)
        {
            var ret = new Dictionary<string, string>();
            (string name, string value) = ReadProperty(rdr);
            var _3dtVersion = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (_3dtVersion == 1.35f)
                _3dtVersion = 1.31f;
            //ret["3dtVersion"] = value;
            MapVersion = _3dtVersion;

            var numStats = 8;
            if (_3dtVersion < 1.34)
                numStats = 6;
            
            for (int i = 0; i < numStats; ++i)
            {
                (name, value) = ReadProperty(rdr);
                ret[name] = value;
            }

            return ret;
        }

        private void WriteMapStats(Dictionary<string, string> stats, List<Solid> solids, List<Entity> entities, DataStructures.MapObjects.Map map, StreamWriter wr)
        {
            WriteProperty("3dtVersion", "1.35", wr);

            //The count on the Visgroups is off by 1 because it is counting Auto. We do not use Auto in 3DT or RFEdit.
            var NumGroups = (map.Visgroups.Count() - 1).ToString();

            //We know what the map header is supposed to look like now
            WriteProperty("TextureLib", stats["TextureLib"], wr, true);
            WriteProperty("HeadersDir", stats["HeadersDir"], wr, true);
            WriteProperty("NumEntities", entities.Count.ToString(), wr);
            WriteProperty("NumModels", stats["NumModels"], wr);
            WriteProperty("NumGroups", NumGroups, wr);
            WriteProperty("Brushlist", solids.Count.ToString(), wr);


            /*
            foreach(var entry in stats)
            {
                // skippers
                if (entry.Key == "ActorsDir" || entry.Key == "PawnIni")
                    continue;

                if (entry.Key == "TextureLib" || entry.Key == "HeadersDir")
                    WriteProperty(entry.Key, entry.Value, wr, true);
                else
                    WriteProperty(entry.Key, entry.Value, wr);
            }
            */
        }

        private List<Motion> ReadMotions(int numMotions, StreamReader rdr)
        {
            const NumberStyles ns = NumberStyles.Float;
            FileStream fs = (FileStream)rdr.BaseStream;
            List<Motion> models = new List<Motion>();

            string line = null;
            bool newModel = true;
            for (int i = 0; i < numMotions; ++i)
            {
                var model = new Motion();

                bool inModel = true;
                while (inModel)
                {
                    var start = rdr.GetPosition();
                    if (line == null)
                        line = rdr.ReadLine();

                    if (!newModel && line.StartsWith("Model \""))
                    {
                        models.Add(model);
                        newModel = true;
                        break;
                    }
                    else if (line.StartsWith("Group \""))
                    {
                        models.Add(model);
                        rdr.SetPosition(start);
                        return models;
                    }

                    model.RawModelLines.Add(line);
                    line = null;
                    newModel = false;
                }
            }

            return models;
        }

        private void WriteMotions(List<Motion> motions, StreamWriter wr)
        {
            foreach (var motion in motions)
            {
                foreach (var line in motion.RawModelLines)
                    wr.WriteLine(line);
                /*WriteProperty("Model", motion.Name, wr, true);
                WriteProperty("ModelId", motion.ID.ToString(), wr, false, 1);
                WriteProperty("CurrentKeyTime", motion.CurrentKeyTime.ToString("0.000000", CultureInfo.InvariantCulture), wr, false, 1);
                WriteProperty("Transform", motion.Transform, wr, false, 1, true);
                WriteProperty("Motion", motion.IsMotion ? "1" : "0", wr, false, 1);
                var nativeMotion = motion.NativeMotion.WriteToString();
                nativeMotion = nativeMotion.Replace("\n\n", "\n");
                wr.Write(nativeMotion);*/
            }
        }

        private List<Visgroup> ReadGroups(int numGroups, StreamReader rdr)
        {
            var ret = new List<Visgroup>();
            for (int i = 0; i < numGroups; ++i)
            {
                var group = new Visgroup();

                (string name, string value) = ReadProperty(rdr);
                group.Name = value;

                (name, value) = ReadProperty(rdr);
                group.ID = int.Parse(value);

                (name, value) = ReadProperty(rdr);
                group.Visible = value == "1";

                (name, value) = ReadProperty(rdr);
                (name, value) = ReadProperty(rdr);
                var colors = value.Split(' ');
                var r = int.Parse(colors[0], NumberStyles.Float, CultureInfo.InvariantCulture);
                var g = int.Parse(colors[1], NumberStyles.Float, CultureInfo.InvariantCulture);
                var b = int.Parse(colors[2], NumberStyles.Float, CultureInfo.InvariantCulture);

                group.Colour = System.Drawing.Color.FromArgb(r, g, b);

                ret.Add(group);
            }

            return ret;
        }

        /// <summary>
        /// Reads a map from a stream in 3DT format.
        /// </summary>
        /// <param name="stream">The stream to read from</param>
        /// <returns>The parsed map</returns>
        protected override DataStructures.MapObjects.Map GetFromStream(Stream stream)
        {
            using (var rdr = new StreamReader(stream))
            {
                var map = new DataStructures.MapObjects.Map();
                var stats = ReadMapStats(rdr);
                map.WorldSpawn.MetaData.Set("stats", stats);

                var allEntities = ReadAllEntities(rdr, map.IDGenerator);
                var worldspawn = allEntities.FirstOrDefault(x => x.EntityData.Name == "worldspawn")
                                 ?? new Entity(0) { EntityData = { Name = "worldspawn" } };
                allEntities.Remove(worldspawn);
                map.WorldSpawn.EntityData = worldspawn.EntityData;
                allEntities.ForEach(x => x.SetParent(map.WorldSpawn, false));
                foreach (var obj in worldspawn.GetChildren().ToArray())
                {
                    obj.SetParent(map.WorldSpawn, false);
                }
                map.WorldSpawn.UpdateBoundingBox(false);

                var numMotions = int.Parse(stats["NumModels"]);
                map.Motions = ReadMotions(numMotions, rdr);
                var numGroups = int.Parse(stats["NumGroups"]);
                map.Visgroups.AddRange(ReadGroups(numGroups, rdr));

                string other = rdr.ReadToEnd();
                map.WorldSpawn.MetaData.Set("stuff", other);

                return map;
            }
        }

        private static void FlattenTree(MapObject parent, List<Solid> solids, List<Entity> entities, List<Group> groups)
        {
            foreach (var mo in parent.GetChildren())
            {
                if (mo is Solid)
                {
                    solids.Add((Solid)mo);
                }
                else if (mo is Entity)
                {
                    entities.Add((Entity)mo);
                }
                else if (mo is Group)
                {
                    groups.Add((Group)mo);
                    FlattenTree(mo, solids, entities, groups);
                }
            }
        }

        protected override void SaveToStream(Stream stream, DataStructures.MapObjects.Map map)
        {
            using (var sw = new StreamWriter(stream))
            {
                // Gather everything we need
                var solids = new List<Solid>();
                var entities = new List<Entity>();
                var groups = new List<Group>();
                var stats = map.WorldSpawn.MetaData.Get<Dictionary<string, string>>("stats");

                // Populate the solids entities and groups
                FlattenTree(map.WorldSpawn, solids, entities, groups);

                // Write Map Header
                WriteMapStats(stats, solids, entities, map, sw);

                
                

                // write world entity aka Brushes
                WriteWorldEntity(solids, sw);

                // write other entities
                WriteProperty("Class", "CEntList", sw);
                WriteProperty("EntCount", entities.Count().ToString(), sw);
                WriteProperty("CurEnt", "0", sw);

                foreach (var entity in entities)
                {
                    WriteEntity(entity, sw);
                }

                // write motions
                WriteMotions(map.Motions, sw);

                // write groups
                foreach (var visgroup in map.Visgroups)
                {
                    // Do not save the group automatically created by sledge
                    if (visgroup.Name == "Auto")
                        continue;

                    WriteProperty("Group", visgroup.Name, sw, true);
                    WriteProperty("GroupId", visgroup.ID.ToString(), sw, false, 1);
                    WriteProperty("Visible", visgroup.Visible ? "1" : "0", sw, false, 1);
                    WriteProperty("Locked", "0", sw, false, 1);
                    WriteProperty("Color", FormatColor(visgroup.Colour), sw);
                }

                sw.Write(map.WorldSpawn.MetaData.Get<string>("stuff"));
            }
        }
    }
}
