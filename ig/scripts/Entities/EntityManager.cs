using System.Collections.Generic;
using System.Linq;
using Godot;
using WebAirIG.IG.Cigi;
using WebAirIG.IG.Core;

namespace WebAirIG.IG.Entities;

/// <summary>Keeps Godot nodes in sync with the CIGI entity state.</summary>
public sealed class EntityManager
{
    private sealed class Instance
    {
        public required Node3D Node;
        public required int TypeId;
        public required ModelTemplate Template;
    }

    private readonly Node3D _root;
    private readonly ModelLibrary _models;
    private readonly Dictionary<int, Instance> _instances = new();

    public EntityManager(Node3D parent, ModelLibrary models)
    {
        _root = new Node3D { Name = "Entities" };
        parent.AddChild(_root);
        _models = models;
    }

    public int Count => _instances.Count;

    public ModelTemplate? TemplateFor(int entityId) => _instances.TryGetValue(entityId, out var i) ? i.Template : null;

    public void Sync(IgState state)
    {
        foreach (var id in _instances.Keys.ToList())
        {
            if (!state.Entities.TryGetValue(id, out var e) || !e.Active || !e.HasPosition || e.TypeId != _instances[id].TypeId)
            {
                _instances[id].Node.QueueFree();
                _instances.Remove(id);
            }
        }

        foreach (var e in state.Entities.Values)
        {
            if (!e.Active || !e.HasPosition) continue;
            if (!_instances.TryGetValue(e.Id, out var inst))
            {
                var template = _models.Get(e.TypeId);
                var node = template.Instantiate();
                node.Name = $"Entity{e.Id}_{template.Type.Name}";
                _root.AddChild(node);
                inst = new Instance { Node = node, TypeId = e.TypeId, Template = template };
                _instances[e.Id] = inst;
                IgLog.Info($"Entity {e.Id} created as {template.Type.Name}");
            }
            inst.Node.Transform = GodotConv.FromNedPose(e.PositionNed, e.NedFromBody);
        }
    }
}
