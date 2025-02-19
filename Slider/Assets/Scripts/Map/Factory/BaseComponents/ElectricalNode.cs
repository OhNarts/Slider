using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

//This is what controls the electrical system for the Factory/any other system that uses it.
public class ElectricalNode : MonoBehaviour
{
    public enum NodeType
    {
        //These determine what type of edges in the graph there are.
        IO,
        INPUT,
        OUTPUT,

        //IO to IO is undirected
        //Directed edges go from INPUT to IO, IO to OUTPUT, and INPUT to OUTPUT
    }

    [Header("Electrical Node")]
    public NodeType nodeType;

    [Tooltip("NEIGHBORS ARE OUTGOING EDGES")]
    [SerializeField] protected List<ElectricalNode> neighbors;
    [SerializeField] private bool powerOnStart;
    [SerializeField] protected bool invertSignal = false;
    [SerializeField] protected bool affectedByBlackout = true;

    [Header("DEBUG TOOLS")]
    [SerializeField] protected bool debugAsPoweredOn;

    protected int powerRefs;
    protected Dictionary<ElectricalNode, int> powerPathPrevs;  //This is used for backtracking paths to a power source. (value is number of times referenced)

    public class OnPoweredArgs
    {
        public bool powered;
    }
    public UnityEvent<OnPoweredArgs> OnPowered;
    public UnityEvent OnPoweredOn;
    public UnityEvent OnPoweredOff;

    private bool blackedOut;

    public virtual bool Powered => !blackedOut && PoweredNormally || debugAsPoweredOn; //This is marked virtual so we can have different powering conditions (see TimedGate.cs)

    protected bool PoweredNormally => (invertSignal ? powerRefs <= 0 : powerRefs > 0);

    public virtual bool AffectedByBlackout => nodeType == NodeType.INPUT && affectedByBlackout && !FactoryGrid.IsInPast(gameObject);
    protected static bool CrystalBlackout => SGrid.Current.GetArea() == Area.Factory && PowerCrystal.Blackout;

    protected void Awake()
    {
        powerPathPrevs = new Dictionary<ElectricalNode, int>();
        powerRefs = 0;  //Always start off and let things turn on.
    }

    protected virtual void OnEnable()
    {
        OnPowered.AddListener(OnPoweredHandler);
    }

    protected virtual void OnDisable()
    {
        OnPowered.RemoveListener(OnPoweredHandler);
    }

    private void Start()
    {
        if (powerOnStart)
        {
            StartSignal(true);
        } else if (Powered)
        {
            //This is mainly for inverted power.
            OnPowered?.Invoke(new OnPoweredArgs { powered = Powered });
        }
    }

    public virtual void OnPoweredHandler(OnPoweredArgs e) { 
        if (e.powered)
        {
            OnPoweredOn?.Invoke();
        } else
        {
            OnPoweredOff?.Invoke();
        }
    }

    public void SetBlackout(bool isBlackout)
    {
        blackedOut = isBlackout;
        OnPowered?.Invoke(new OnPoweredArgs { powered = Powered });

        PushSignalToOutput(!isBlackout && PoweredNormally);
    }

    public virtual void StartSignal(bool input, bool includeSelf = true)
    {
        if (nodeType != NodeType.INPUT)
        {
            //According to the OOD gurus and lord Aibek, this violates some design principle (I think). Oh well.
            Debug.LogError("Can only start a signal from an INPUT node.");
        }

        if (Powered != input)    //This ensures we don't double propagate
        {
            if (includeSelf)
            {
                powerRefs = input ? 1 : 0;
            }

            OnPowered?.Invoke(new OnPoweredArgs { powered = Powered });

            PushSignalToOutput(input);
        }
    }

    //Target Complexity : O(n) including recursive calls (so updating node state should be O(1))
    protected virtual void PropagateSignal(bool value, ElectricalNode prev, HashSet<ElectricalNode> recStack, int numRefs = 1)
    {
        //These two methods are split in order to implement buffered input (which is only used for timed gates).
        bool oldPowered = Powered;
        if (EvaluateNodeInput(value, prev, recStack, numRefs))
        {
            //Call the event/handlers (this should only be used for nodes to respond to inputs, NOT to update node state)
            if (Powered != oldPowered)
            {
                OnPowered?.Invoke(new OnPoweredArgs { powered = Powered });
            }

            PushSignalToOutput(value, recStack, numRefs);
        }

    }

    //Takes the signal in, updates the node's state (powerRefs, powerPathPrevs)
    protected bool EvaluateNodeInput(bool value, ElectricalNode prev, HashSet<ElectricalNode> recStack, int numRefs = 1)
    {
        if (nodeType == NodeType.INPUT)
        {
            Debug.LogError("Cannot propogate a signal through an INPUT Node Type.");
        }

        if (recStack.Contains(this))
        {  //Cycle Detection
            return false;
        }

        if (value)
        {
            powerRefs = powerRefs + numRefs;
            if (!powerPathPrevs.ContainsKey(prev))
            {
                powerPathPrevs.Add(prev, 0);
            }
            powerPathPrevs[prev] += numRefs;
        }
        else
        {
            powerRefs = Mathf.Max(powerRefs - numRefs, 0);
            if (powerPathPrevs.ContainsKey(prev)) {
                powerPathPrevs[prev] -= numRefs;
                if (powerPathPrevs[prev] <= 0)
                {
                    powerPathPrevs.Remove(prev);
                }
            }
        }

        return true;
    }

    //Takes the existing signal on the node and pushes it to the node's neighbors.
    protected void PushSignalToOutput(bool value, HashSet<ElectricalNode> recStack = null, int numRefs = 1)
    {
        if (recStack == null)
        {
            recStack = new HashSet<ElectricalNode>();
        }

        //Propagate new signal to all other neighbors
        recStack.Add(this);
        foreach (ElectricalNode neighbor in neighbors)
        {
            if (!recStack.Contains(neighbor))
            {
                neighbor.PropagateSignal(value, this, recStack, numRefs);
            }
        }
        recStack.Remove(this);
    }

    //Complexity is O(n * p) where p is the number of paths (which shouldn't be too many usually)
    public void GetPathNodes(out List<HashSet<ElectricalNode>> nodes)
    {
        nodes = new List<HashSet<ElectricalNode>>();

        if (powerPathPrevs.Count > 0)
        {
            nodes.Add(new HashSet<ElectricalNode>());

            GetPathNodesRecursive(nodes);
        } else if (powerRefs > 0 && nodeType == NodeType.INPUT)   //This is an input powered node
        {
            nodes.Add(new HashSet<ElectricalNode>());
            nodes[0].Add(this);
        }
    }

    private void GetPathNodesRecursive(List<HashSet<ElectricalNode>> allPathNodes)
    {
        HashSet<ElectricalNode> currPathNodes = allPathNodes[allPathNodes.Count-1];
        currPathNodes.Add(this);

        if (powerPathPrevs.Count > 1)
        {
            //Path Branches
            var rootPath = new HashSet<ElectricalNode>(currPathNodes);

            allPathNodes.RemoveAt(allPathNodes.Count - 1);
            foreach (var node in powerPathPrevs.Keys)
            {
                if (!currPathNodes.Contains(node))
                {
                    allPathNodes.Add(new HashSet<ElectricalNode>(rootPath));
                    node.GetPathNodesRecursive(allPathNodes);
                }

            }
        } else
        {
            //Continue current path
            foreach (var node in powerPathPrevs.Keys)
            {
                if (!currPathNodes.Contains(node))
                {
                    node.GetPathNodesRecursive(allPathNodes);
                }

            }
        }
    }

    //Target Complexity: O(n * p) p is the number of paths in this node as well as other (i.e. refs)
    //This method needs to add the neighbor AND update the power refs/paths of other nodes.
    public virtual bool AddNeighbor(ElectricalNode other)
    {
        if (other == null)
        {
            Debug.LogError("You cannot add a null neighbor to ElectricalNode");
            return false;
        }

        if (neighbors.Contains(other) || other.neighbors.Contains(this))
        {
            return false;
        }

        if (this.nodeType == NodeType.IO && other.nodeType == NodeType.IO)
        {
            //Undirected case (Note: I'm using "this" mainly for readability to distinguish from other and show the parallelism)

            //Get all of the possible paths from power sources to the two nodes BEFORE 
            List<HashSet<ElectricalNode>> pathsFromOther;
            other.GetPathNodes(out pathsFromOther);
            List<HashSet<ElectricalNode>> pathsFromThis;
            this.GetPathNodes(out pathsFromThis);

            //Propagate each path individually
            foreach (var path in pathsFromOther)
            {
                this.PropagateSignal(true, other, path, 1);
            }
            foreach (var path in pathsFromThis)
            {
                other.PropagateSignal(true, this, path, 1);
            }

            //Create an undirected edge between the two nodes
            this.neighbors.Add(other);
            other.neighbors.Add(this);
        } else if (this.nodeType != NodeType.OUTPUT && other.nodeType != NodeType.INPUT)
        {
            //Directed edge from this to other.
            PropagateAllPathsFromTo(true, this, other);
            this.neighbors.Add(other);
        } else if (this.nodeType != NodeType.INPUT && other.nodeType != NodeType.OUTPUT)
        {
            //Directed edge from other to this
            PropagateAllPathsFromTo(true, other, this);
            other.neighbors.Add(this);
        } else
        {
            //Any other cases are essentially not allowed.
            Debug.LogError("Attempted to create a connection going out of an output node or into an input node, this is not allowed.");
            return false;
        }

        return true;
    }

    //Target Complexity : O(n) p is the number of paths in this node as well as other (i.e. refs)
    public virtual bool RemoveNeighbor(ElectricalNode other)
    {
        //The neighbor is already removed, this prevents double counting.
        if (!neighbors.Contains(other) && !other.neighbors.Contains(this))
        {
            //Debug.Log($"{gameObject.name} does not have an edge including {other.gameObject.name}. This method does nothing.");
            return false;
        }

        //Debug.Log($"Removing Node {other.gameObject} from node {this.gameObject}");

        if (this.nodeType == NodeType.IO && other.nodeType == NodeType.IO)
        {
            //Undirected Case

            //The procedure goes in the opposite order as AddNeighbor, so we remove the edge first.
            this.neighbors.Remove(other);
            other.neighbors.Remove(this);

            this.powerPathPrevs.Remove(other);
            other.powerPathPrevs.Remove(this);

            List<HashSet<ElectricalNode>> pathsFromOther;
            other.GetPathNodes(out pathsFromOther);
            List<HashSet<ElectricalNode>> pathsFromThis;
            this.GetPathNodes(out pathsFromThis);

            foreach (var path in pathsFromOther)
            {
                //Deletes ("Breaks") the paths that pass from other to this
                this.PropagateSignal(false, other, path, 1);
            }

            foreach (var path in pathsFromThis)
            {
                other.PropagateSignal(false, this, path, 1);
            }
        }
        else if (this.nodeType != NodeType.OUTPUT && other.nodeType != NodeType.INPUT)
        {
            //Directed edge from this to other.
            this.neighbors.Remove(other);
            PropagateAllPathsFromTo(false, this, other);
        }
        else if (this.nodeType != NodeType.INPUT && other.nodeType != NodeType.OUTPUT)
        {
            //Directed edge from other to this
            other.neighbors.Remove(this);
            PropagateAllPathsFromTo(false, other, this);
        }
        else
        {
            //Any other cases are essentially not allowed.
            Debug.LogError("Attempted to remove a connection going out of an output node or into an input node, this is not allowed.");
            return false;
        }

        return true;
    }

    public void RemoveAllNeighbors()
    {
        while(neighbors.Count > 0) 
        {
            RemoveNeighbor(neighbors[0]);
        }
    }

    private static void PropagateAllPathsFromTo(bool value, ElectricalNode from, ElectricalNode to)
    {
        List<HashSet<ElectricalNode>> pathsFrom;
        from.GetPathNodes(out pathsFrom);

        //Propagate each path individually
        foreach (var path in pathsFrom)
        {
            to.PropagateSignal(value, from, path, 1);
        }
    }
}