using System;
using System.Collections.Generic;
using UnityEngine;

namespace ConnectedMining
{
    internal sealed class Shape
    {
        internal readonly Bounds Bounds;
        private readonly Triangle[] triangles;
        private readonly Triangle? axis;
        private readonly double radius;
        private static Vec V(Vector3 v) => new Vec(v.x, v.y, v.z);

        private Shape(Bounds bounds, Triangle[] mesh, Triangle? segment = null, double r = 0)
        { Bounds = bounds; triangles = mesh; axis = segment; radius = r; }

        internal static Shape Capture(Collider collider)
        {
            if (collider is SphereCollider sphere)
            {
                var c = V(sphere.transform.TransformPoint(sphere.center));
                var scale = sphere.transform.lossyScale;
                return new Shape(collider.bounds, null, new Triangle(c, c, c), sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            }
            if (collider is CapsuleCollider capsule)
            {
                Vector3 scale = capsule.transform.lossyScale;
                float lengthScale = Mathf.Abs(scale[capsule.direction]);
                float r = capsule.radius * Mathf.Max(Mathf.Abs(scale[(capsule.direction + 1) % 3]), Mathf.Abs(scale[(capsule.direction + 2) % 3]));
                Vector3 direction = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
                Vector3 offset = capsule.transform.rotation * direction * Mathf.Max(0, capsule.height * lengthScale * 0.5f - r);
                Vector3 center = capsule.transform.TransformPoint(capsule.center);
                Vec a = V(center - offset), b = V(center + offset);
                return new Shape(collider.bounds, null, new Triangle(a, b, b), r);
            }
            Vector3[] vertices;
            int[] indices;
            if (collider is MeshCollider meshCollider && meshCollider.sharedMesh)
            {
                Mesh mesh = meshCollider.sharedMesh;
                // Do not substitute a bounding box: that could connect rocks across visible gaps.
                if (!mesh.isReadable)
                {
                    Plugin.Instance.WarnUnreadable(mesh.name);
                    return null;
                }
                vertices = mesh.vertices;
                indices = mesh.triangles;
            }
            else if (collider is BoxCollider box)
            {
                vertices = new Vector3[8];
                for (int i = 0; i < 8; i++)
                    vertices[i] = box.center + Vector3.Scale(box.size * 0.5f, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                indices = new[] { 0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5 };
            }
            else return null;
            var world = new Vec[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) world[i] = V(collider.transform.TransformPoint(vertices[i]));
            var triangles = new Triangle[indices.Length / 3];
            for (int i = 0; i < triangles.Length; i++) triangles[i] = new Triangle(world[indices[i * 3]], world[indices[i * 3 + 1]], world[indices[i * 3 + 2]]);
            return new Shape(collider.bounds, triangles);
        }

        internal bool Touches(Shape other, float tolerance)
        {
            Bounds expanded = Bounds;
            expanded.Expand(tolerance * 2);
            if (!expanded.Intersects(other.Bounds)) return false;
            if (!axis.HasValue && !other.axis.HasValue) return Geometry.Touches(triangles, other.triangles, tolerance);
            if (axis.HasValue && other.axis.HasValue)
                return Geometry.DistanceSquared(axis.Value, other.axis.Value) <= Math.Pow(radius + other.radius + tolerance, 2);
            Shape round = axis.HasValue ? this : other;
            Shape mesh = axis.HasValue ? other : this;
            double distance = Math.Pow(round.radius + tolerance, 2);
            foreach (Triangle t in mesh.triangles)
                if (Geometry.DistanceSquared(round.axis.Value, t) <= distance) return true;
            return Geometry.Touches(new[] { round.axis.Value }, mesh.triangles, 0);
        }

        internal static List<Shape> Capture(Deposit node)
        {
            var result = new List<Shape>();
            if (node.Owner is Destructible)
            {
                foreach (Collider c in node.Owner.GetComponentsInChildren<Collider>())
                {
                    Deposit d = Deposit.FromCollider(c);
                    if (c.enabled && d != null && d.Key == node.Key)
                    {
                        Shape shape = Capture(c);
                        if (shape != null) result.Add(shape);
                    }
                }
            }
            else
            {
                Shape shape = Capture(node.Collider);
                if (shape != null) result.Add(shape);
            }
            return result;
        }
    }
}
