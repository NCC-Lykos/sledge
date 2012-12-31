﻿using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using Sledge.DataStructures.Geometric;
using Sledge.DataStructures.MapObjects;
using Sledge.Editor.Editing;
using Sledge.Editor.Properties;
using Sledge.Editor.Rendering;
using Sledge.UI;
using Matrix = Sledge.Graphics.Helpers.Matrix;

namespace Sledge.Editor.Tools
{
    public class VMTool : BaseBoxTool
    {
        private class VMPoint
        {
            private bool _isSelected;
            public Coordinate Coordinate { get; set; }
            public Solid Solid { get; set; }
            public List<Vertex> Vertices { get; set; }

            /// <summary>
            /// Midpoints are somewhat "virtual" points in that they only facilitate the moving and selection of two points at once.
            /// </summary>
            public bool IsMidPoint { get; set; }
            public VMPoint MidpointStart { get; set; }
            public VMPoint MidpointEnd { get; set; }
            public bool IsSelected
            {
                get
                {
                    if (IsMidPoint) return MidpointStart.IsSelected && MidpointEnd.IsSelected;
                    return _isSelected;
                }
                set
                {
                    if (IsMidPoint) MidpointStart.IsSelected = MidpointEnd.IsSelected = value;
                    else _isSelected = value;
                }
            }

            public void Move(Coordinate delta)
            {
                Coordinate += delta;
                if (!IsMidPoint)
                {
                    Vertices.ForEach(x => x.Location += delta);
                }
            }

            public Color GetColour()
            {
                // Midpoints are selected = Pink, deselected = orange
                // Vertex points are selected = red, deselected = white
                if (IsMidPoint) return IsSelected ? Color.DeepPink : Color.Orange;
                return IsSelected ? Color.Red : Color.White;
            }
        }

        private enum VMState
        {
            None,
            Moving
        }

        /// <summary>
        /// Key = copy, Value = original
        /// </summary>
        private Dictionary<Solid, Solid> _copies; 
        private List<VMPoint> _points;
        private VMState _state;
        private Coordinate _moveStart;
        private VMPoint _clickedPoint;

        public override Image GetIcon()
        {
            return Resources.Tool_VM;
        }

        public override string GetName()
        {
            return "Vertex Manipulation Tool";
        }

        protected override Color BoxColour
        {
            get { return Color.Orange; }
        }

        protected override Color FillColour
        {
            get { return Color.FromArgb(64, Color.DodgerBlue); }
        }

        /// <summary>
        /// Get the topmost (for the supplied viewport) VM point at the provided coordinate.
        /// </summary>
        /// <param name="x">The X coordinate</param>
        /// <param name="y">The Y coordinate</param>
        /// <param name="viewport">The viewport</param>
        /// <returns>The topmost point, or null if no point found</returns>
        private VMPoint GetVertexAtPoint(int x, int y, Viewport2D viewport)
        {
            var p = viewport.ScreenToWorld(x, y);
            var d = 5 / viewport.Zoom; // Tolerance value = 5 pixels

            // Order by the unused coordinate in the view (which is the up axis) descending to get the "closest" point
            return (from point in _points
                    let c = viewport.Flatten(point.Coordinate)
                    where p.X >= c.X - d && p.X <= c.X + d && p.Y >= c.Y - d && p.Y <= c.Y + d
                    let unused = viewport.GetUnusedCoordinate(point.Coordinate)
                    orderby unused.X + unused.Y + unused.Z descending
                    select point).FirstOrDefault();
        }

        public override void ToolSelected()
        {
            var selectedSolids = Selection.GetSelectedObjects().OfType<Solid>().ToList();
            // Init the points and copy caches
            _copies = new Dictionary<Solid, Solid>();
            _points = new List<VMPoint>();
            foreach (var solid in selectedSolids)
            {
                var copy = (Solid) solid.Clone();
                _copies.Add(copy, solid);

                // Set all the original solids to hidden
                // (do this after we clone it so the clones aren't hidden too)
                solid.IsHidden = true;

                // Add the vertex points
                // Group by location per solid, duplicate coordinates are "attached" and moved at the same time
                foreach (var group in copy.Faces.SelectMany(x => x.Vertices).GroupBy(x => x.Location))
                {
                    _points.Add(new VMPoint
                                    {
                                        Solid = copy, // ten four, solid copy
                                        Coordinate = group.First().Location,
                                        Vertices = group.ToList()
                                    });
                }
            }
            RefreshMidpoints();
            _state = VMState.None;
            _moveStart = null;
            Document.UpdateDisplayLists(); // Can't just update the select list because the solids are now transparent
        }

        public override void ToolDeselected()
        {
            // The solids are no longer hidden
            var selectedSolids = Selection.GetSelectedObjects().OfType<Solid>().ToList();
            foreach (var o in selectedSolids)
            {
                o.IsHidden = false;
                // Commit the manips back into the original object
                var copy = _copies.First(x => x.Value == o).Key;
                o.Unclone(copy);
                o.Faces.ForEach(x => x.UpdateBoundingBox());
                o.UpdateBoundingBox();
            }
            _copies = null;
            _points = null;
            _state = VMState.None;
            _moveStart = null;
            Document.UpdateDisplayLists();
        }

        /// <summary>
        /// Updates the positions of all midpoints.
        /// </summary>
        private void RefreshMidpoints()
        {
            _points.RemoveAll(x => x.IsMidPoint);
            foreach (var copy in _copies.Keys)
            {
                foreach (var group in copy.Faces.SelectMany(x => x.GetLines()).GroupBy(x => new { x.Start, x.End }))
                {
                    _points.Add(new VMPoint
                    {
                        Solid = copy,
                        Coordinate = (group.Key.Start + group.Key.End) / 2,
                        IsMidPoint = true,
                        MidpointStart = _points.First(x => !x.IsMidPoint && x.Coordinate == group.Key.Start),
                        MidpointEnd = _points.First(x => !x.IsMidPoint && x.Coordinate == group.Key.End)
                    });
                }
            }
        }

        public override void MouseDown(ViewportBase vp, MouseEventArgs e)
        {
            if (!(vp is Viewport2D))
            {
                base.MouseDown(vp, e);
                return;
            }

            var viewport = (Viewport2D)vp;

            // Find the clicked vertex
            var vtx = GetVertexAtPoint(e.X, viewport.Height - e.Y, viewport);
            if (vtx == null)
            {
                // Nothing clicked
                if (!KeyboardState.Ctrl)
                {
                    // Deselect all the point if not ctrl-ing
                    _points.ForEach(x => x.IsSelected = false);
                }
                base.MouseDown(vp, e);
                return;
            }

            // Vertex found, cancel the box if needed
            BoxDrawnCancel(vp);

            // Mouse down on a point
            _state = VMState.Moving;
            Document.CaptureAltPresses = true;
            if (!vtx.IsSelected && !KeyboardState.Ctrl)
            {
                // If we aren't clicking on a selected point and ctrl is not down, deselect the others
                _points.ForEach(x => x.IsSelected = false);
                // If this point is already selected, don't deselect others. This is so we can move multiple points easily.
            }
            vtx.IsSelected = true;
            _clickedPoint = vtx; // This is unset if the mouse is moved, see MouseUp logic.
            _moveStart = SnapIfNeeded(viewport.Expand(viewport.ScreenToWorld(e.X, viewport.Height - e.Y)));
        }

        public override void MouseUp(ViewportBase viewport, MouseEventArgs e)
        {
            base.MouseUp(viewport, e);

            if (!(viewport is Viewport2D)) return;

            if (_state == VMState.Moving && _clickedPoint != null && !KeyboardState.Ctrl)
            {
                // If we were moving (or clicking on) a point, and the clicked point is
                // not null (mouse not moved yet), AND ctrl is not down, deselect the other points.
                // Otherwise selection has already been handled by MouseDown.
                _points.ForEach(x => x.IsSelected = false);
                _clickedPoint.IsSelected = true;
            }

            if (_state == VMState.Moving)
            {
                CheckMergedVertices();
            }
            
            RefreshMidpoints();
            _state = VMState.None;
            _moveStart = null;
            _clickedPoint = null;
            Document.CaptureAltPresses = false;
        }

        private void CheckMergedVertices()
        {
            // adjacent points with the same solid and coordinate need to be merged (erp)
            foreach (var group in _points.Where(x => !x.IsMidPoint).GroupBy(x => new {x.Solid, x.Coordinate}).Where(x => x.Count() > 1))
            {
                var count = group.Count();
                var allFaces = group.SelectMany(x => x.Vertices).Select(x => x.Parent).ToList();
                // ... this is hard :(
            }
        }

        public override void MouseMove(ViewportBase vp, MouseEventArgs e)
        {
            base.MouseMove(vp, e);

            if (!(vp is Viewport2D)) return;
            var viewport = (Viewport2D)vp;

            if (_state == VMState.None)
            {
                // Not moving a point, just test for the cursor.
                var vtx = GetVertexAtPoint(e.X, viewport.Height - e.Y, viewport);
                if (vtx != null) viewport.Cursor = Cursors.Cross;
                else if (viewport.Cursor == Cursors.Cross) viewport.Cursor = Cursors.Default;
            }
            else
            {
                // Moving a point, get the delta moved
                var point = SnapIfNeeded(viewport.Expand(viewport.ScreenToWorld(e.X, viewport.Height - e.Y)));
                var moveDistance = point - _moveStart;
                _moveStart = point;
                // Move each selected point by the delta value
                foreach (var p in _points.Where(x => x.IsSelected))
                {
                    p.Move(moveDistance);
                }
                RefreshMidpoints();
                // We've moved the mouse, so not clicking on a point.
                _clickedPoint = null;
            }
        }

        public override void BoxDrawnConfirm(ViewportBase viewport)
        {
            Box box;
            if (GetSelectionBox(out box))
            {
                foreach (var point in _points.Where(x => box.CoordinateIsInside(x.Coordinate)))
                {
                    // Select all the points in the box
                    point.IsSelected = true;
                }
            }

            base.BoxDrawnConfirm(viewport);
        }

        protected override void Render2D(Viewport2D vp)
        {
            base.Render2D(vp);

            // Render out the solid previews
            GL.Color3(Color.Pink);
            Graphics.Helpers.Matrix.Push();
            var matrix = DisplayListGroup.GetMatrixFor(vp.Direction);
            GL.MultMatrix(ref matrix);
            DataStructures.Rendering.Rendering.DrawWireframe(_copies.Keys.SelectMany(x => x.Faces), true);
            Graphics.Helpers.Matrix.Pop();

            // Draw in order by the unused coordinate (the up axis for this viewport)
            var ordered = (from point in _points
                           let unused = vp.GetUnusedCoordinate(point.Coordinate)
                           orderby unused.X + unused.Y + unused.Z
                           select point).ToList();
            // Render out the point handles
            var z = (double) vp.Zoom;
            GL.Begin(BeginMode.Quads);
            foreach (var point in ordered)
            {
                var c = vp.Flatten(point.Coordinate);
                GL.Color3(Color.Black);
                GLX.Square(new Vector2d(c.DX, c.DY), 4, z, true);
                GL.Color3(point.GetColour());
                GLX.Square(new Vector2d(c.DX, c.DY), 3, z, true);
            }
            GL.End();
        }

        protected override void Render3D(Viewport3D vp)
        {
            base.Render3D(vp);

            // Get us into 2D rendering
            Matrix.Set(MatrixMode.Projection);
            Matrix.Identity();
            Graphics.Helpers.Viewport.Orthographic(0, 0, vp.Width, vp.Height);
            Matrix.Set(MatrixMode.Modelview);
            Matrix.Identity();

            var half = new Coordinate(vp.Width, vp.Height, 0) / 2;
            // Render out the point handles
            GL.Begin(BeginMode.Quads);
            foreach (var point in _points)
            {
                var c = vp.WorldToScreen(point.Coordinate);
                if (c.Z > 1) continue;
                c -= half;

                GL.Color3(Color.Black);
                GL.Vertex2(c.DX - 4, c.DY - 4);
                GL.Vertex2(c.DX - 4, c.DY + 4);
                GL.Vertex2(c.DX + 4, c.DY + 4);
                GL.Vertex2(c.DX + 4, c.DY - 4);

                GL.Color3(point.GetColour());
                GL.Vertex2(c.DX - 3, c.DY - 3);
                GL.Vertex2(c.DX - 3, c.DY + 3);
                GL.Vertex2(c.DX + 3, c.DY + 3);
                GL.Vertex2(c.DX + 3, c.DY - 3);
            }
            GL.End();

            // Get back into 3D rendering
            Matrix.Set(MatrixMode.Projection);
            Matrix.Identity();
            Graphics.Helpers.Viewport.Perspective(0, 0, vp.Width, vp.Height);
            Matrix.Set(MatrixMode.Modelview);
            Matrix.Identity();
            vp.Camera.Position();

            // Render out the solid previews
            GL.Color3(Color.White);
            var faces = _copies.Keys.SelectMany(x => x.Faces).ToList();
            DataStructures.Rendering.Rendering.DrawFilled(faces, Color.Empty);
            DataStructures.Rendering.Rendering.DrawFilled(faces, Color.FromArgb(64, Color.Green));
            GL.Color3(Color.Pink);
            DataStructures.Rendering.Rendering.DrawWireframe(faces, true);
        }
    }
}