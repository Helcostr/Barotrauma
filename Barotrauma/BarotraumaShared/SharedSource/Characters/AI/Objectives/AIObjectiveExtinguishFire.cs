using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using System;
using System.Linq;
using Barotrauma.Extensions;
using FarseerPhysics;

namespace Barotrauma
{
    class AIObjectiveExtinguishFire : AIObjective
    {
        public override Identifier Identifier { get; set; } = "extinguish fire".ToIdentifier();
        public override bool ForceRun => true;
        protected override bool ConcurrentObjectives => true;
        public override bool KeepDivingGearOn => true;
        protected override bool AllowInAnySub => true;
        protected override bool AllowWhileHandcuffed => false;

        private readonly Hull targetHull;

        private AIObjectiveGetItem getExtinguisherObjective;
        private AIObjectiveGoTo gotoObjective;
        // Enum for left or right side of fire to fight
        private enum FightingSide { None, Left, Right };
        private bool runningAway = false;
        private FightingSide fightingSide = FightingSide.None;
        private Vector2 sideOfFire;
        private MovingTarget sideOfFireTarget;
        class MovingTarget : Entity
        {
            private readonly Func<Vector2> posRef;
            public MovingTarget(Func<Vector2> posRef, Submarine submarine) : base(submarine,0) {
                this.posRef = posRef;
            }
            public override Vector2 Position => posRef();
            public override Vector2 SimPosition => ConvertUnits.ToSimUnits(Position);
        }

        public AIObjectiveExtinguishFire(Character character, Hull targetHull, AIObjectiveManager objectiveManager, float priorityModifier = 1) 
            : base(character, objectiveManager, priorityModifier)
        {
            this.targetHull = targetHull;
            SetWaypoint(targetHull.Position);
            sideOfFireTarget = new MovingTarget(() => sideOfFire, targetHull.Submarine);
        }

        private void SetWaypoint(Vector2 position) { sideOfFire = position; }

        protected override float GetPriority()
        {
            if (runningAway) return 0;
            if (!IsAllowed)
            {
                HandleDisallowed();
                return Priority;
            }
            bool isOrder = objectiveManager.HasOrder<AIObjectiveExtinguishFires>();
            if (!isOrder && Character.CharacterList.Any(c => c.CurrentHull == targetHull && !HumanAIController.IsFriendly(c) && HumanAIController.IsActive(c)))
            {
                // Don't go into rooms with any enemies, unless it's an order
                Priority = 0;
                Abandon = true;
                return Priority;
            }
            // Prioritize fires that currently damage the character.
            bool inDamageRange = targetHull.FireSources.Any(fs => fs.IsInDamageRange(character, fs.DamageRange));
            float severity = inDamageRange ? 1.0f : AIObjectiveExtinguishFires.GetFireSeverity(targetHull);
            float characterY = character.CurrentHull?.WorldPosition.Y ?? character.WorldPosition.Y;
            float distanceFactor = targetHull == character.CurrentHull ? 1.0f 
                : HumanAIController.VisibleHulls.Contains(targetHull) ? 0.75f : 0.0f;
            
            if (distanceFactor <= 0.0f)
            {
                distanceFactor = 
                    GetDistanceFactor(
                        new Vector2(character.WorldPosition.Y, characterY),
                        targetHull.WorldPosition,
                        verticalDistanceMultiplier: 3,
                        maxDistance: 5000,
                        factorAtMaxDistance: 0.1f);
            }
            
            if (!inDamageRange && severity > 0.75f && distanceFactor < 0.75f && !isOrder && character.IsOnPlayerTeam &&
                targetHull.RoomName != null &&
                !targetHull.RoomName.Contains("reactor", StringComparison.OrdinalIgnoreCase) && 
                !targetHull.RoomName.Contains("engine", StringComparison.OrdinalIgnoreCase) && 
                !targetHull.RoomName.Contains("command", StringComparison.OrdinalIgnoreCase))
            {
                // Bots in the player crew ignore severe fires that are not close to the target to prevent casualties unless ordered to extinguish.
                Priority = 0;
                Abandon = true;
                return Priority;
            }
            float devotion = CumulatedDevotion / 100;
            Priority = MathHelper.Lerp(0, AIObjectiveManager.MaxObjectivePriority, MathHelper.Clamp(devotion + (severity * distanceFactor * PriorityModifier), 0, 1));
            return Priority;
        }

        protected override bool CheckObjectiveState() => targetHull.FireSources.None();

        private float sinTime;
        private float lastDmgTime;
        private const float dmgDelayMove = 0.5f;
        private int debugMoveCloser = 0;

        private const int cacheSize = 5;
        private WayPoint[] cachePathNode = new WayPoint[cacheSize];

        private void UpdateOldPath()
        {
            if (character.AIController is HumanAIController)
            {
                SteeringPath path = (character.AIController as HumanAIController).PathSteering.CurrentPath;
                if (path == null || path.CurrentNode == null) return;
                if (path.CurrentNode.ID == cachePathNode[cacheSize - 1]?.ID) return;
                for (int i = 0; i < cacheSize - 1; i++) { cachePathNode[i] = cachePathNode[i+1]; }
                cachePathNode[cacheSize - 1] = path.CurrentNode;
                DebugConsoleNewMessage($"Old path index: {cachePathNode[cacheSize-2]?.ID}, current path index: {cachePathNode[cacheSize-1]?.ID}");
            }
        }
        protected override void Act(float deltaTime)
        {
            lastDmgTime += deltaTime;
            var extinguisherItem = character.Inventory.FindItemByTag(Tags.FireExtinguisher);
            if (extinguisherItem == null || extinguisherItem.Condition <= 0.0f || !character.HasEquippedItem(extinguisherItem))
            {
                TryAddSubObjective(ref getExtinguisherObjective, () => {
                    if (character.IsOnPlayerTeam && !character.HasEquippedItem(Tags.FireExtinguisher, allowBroken: false))
                    {
                        character.Speak(TextManager.Get("DialogFindExtinguisher").Value, null, 2.0f, Tags.FireExtinguisher, 30.0f);
                    }
                    var getItemObjective = new AIObjectiveGetItem(character, Tags.FireExtinguisher, objectiveManager, equip: true)
                    {
                        AllowStealing = true,
                        // If the item is inside an unsafe hull, decrease the priority
                        GetItemPriority = i => HumanAIController.UnsafeHulls.Contains(i.CurrentHull) ? 0.1f : 1
                    };
                    if (objectiveManager.HasOrder<AIObjectiveExtinguishFires>())
                    {
                        getItemObjective.Abandoned += () => character.Speak(TextManager.Get("dialogcannotfindfireextinguisher").Value, null, 0.0f, "dialogcannotfindfireextinguisher".ToIdentifier(), 10.0f);
                    };
                    return getItemObjective;
                });
            }
            else
            {
                var extinguisher = extinguisherItem.GetComponent<RepairTool>();
                if (extinguisher == null)
                {
#if DEBUG
                    DebugConsole.ThrowError($"{character.Name}: AIObjectiveExtinguishFire failed - the item \"" + extinguisherItem + "\" has no RepairTool component but is tagged as an extinguisher");
#endif
                    Abandon = true;
                    return;
                }
                UpdateOldPath();
                foreach (FireSource fs in targetHull.FireSources)
                {
                    if (fs == null) { continue; }
                    if (fs.Removed) { continue; }
                    if (character.CurrentHull == null)
                    {
                        Abandon = true;
                        break;
                    }
                    if (fightingSide == FightingSide.None && character.CanSeeTarget(fs))
                        fightingSide = fs.WorldPosition.X + fs.Size.X / 2f < character.WorldPosition.X ? FightingSide.Right : FightingSide.Left;

                    if (fightingSide == FightingSide.Left)
                    {
                        SetWaypoint(fs.Position);
                    }
                    else if (fightingSide == FightingSide.Right)
                    {
                        SetWaypoint(fs.Position + new Vector2(fs.Size.X, 0));
                    }
                    float distSqr = Vector2.DistanceSquared(character.WorldPosition, sideOfFireTarget.WorldPosition);
                    bool inRange = distSqr < extinguisher.Range * extinguisher.Range;
                    bool isInDamageRange = fs.IsInDamageRange(character, fs.DamageRange) && character.CanSeeTarget(targetHull);
                    if (isInDamageRange) lastDmgTime = 0;
                    bool moveCloser = !isInDamageRange && lastDmgTime > dmgDelayMove;
                    bool operateExtinguisher = !moveCloser || (inRange && character.CanSeeTarget(sideOfFireTarget));
                    if (operateExtinguisher)
                    {
                        character.CursorPosition = fs.Position + new Vector2(fs.Size.X, 0);
                        Vector2 fromCharacterToFireSource = fs.WorldPosition - character.WorldPosition;
                        // Because AI can shoot fire from below, sine motion doesn't make sense. Need to readjust  the sin math to only what is visible.
                        // character.CursorPosition += VectorExtensions.Forward(extinguisherItem.body.TransformedRotation + (float)Math.Sin(sinTime) / 2, fromCharacterToFireSource.Length() / 2);
                        if (extinguisherItem.RequireAimToUse)
                        {
                            character.SetInput(InputType.Aim, false, true);
                            sinTime += deltaTime * 10;
                        }
                        character.SetInput(extinguisherItem.IsShootable ? InputType.Shoot : InputType.Use, false, true);
                        extinguisher.Use(deltaTime, character);
                        if (!targetHull.FireSources.Contains(fs))
                        {
                            character.Speak(TextManager.GetWithVariable("DialogPutOutFire", "[roomname]", targetHull.DisplayName, FormatCapitals.Yes).Value, null, 0, "putoutfire".ToIdentifier(), 10.0f);
                        }
                        // Prevents running into the flames.
                        objectiveManager.CurrentObjective.ForceWalk = true;
                    }
                    if (isInDamageRange)
                    {
                        
                        if (!runningAway && gotoObjective != null)
                        {
                            gotoObjective.Abandoned -= GoToAbandoned;
                            gotoObjective.Abandon = true;
                            RemoveSubObjective(ref gotoObjective);
                        }
                        if (!runningAway && gotoObjective == null && TryAddSubObjective(ref gotoObjective, () =>
                        {
                            runningAway = true;
                            DebugConsoleNewMessage($"Ouchie, fleeing {runningAway}.");
                            
                            return new AIObjectiveGoTo(cachePathNode[0], character, objectiveManager,priorityModifier: AIObjectiveManager.MaxObjectivePriority)
                            {
                                Priority = AIObjectiveManager.MaxObjectivePriority,
                                // Owwie, I'm on fire
                                AbortCondition = (obj) => !fs.IsInDamageRange(character, fs.DamageRange) || !character.CanSeeTarget(sideOfFireTarget),
                            };
                        }, onAbandon: GoToAbandoned, onCompleted: () =>
                        {
                            RemoveSubObjective(ref gotoObjective);
                            DebugConsoleNewMessage("Going to waypoint done.");
                            runningAway = false;
                        }))
                        {
                            DebugConsoleNewMessage($"Created runaway.");
                            // list all sub objectives in debug console new message
                            foreach (var subObjective in SubObjectives)
                            {
                                if (subObjective is AIObjectiveGoTo focusedObjective)
                                {
                                    DebugConsoleNewMessage($"Subobjective: {subObjective.Identifier} - {subObjective.GetType().Name}, Destination ID: {(focusedObjective.Target as WayPoint).ID}");
                                }
                                else
                                {
                                    DebugConsoleNewMessage($"Subobjective: {subObjective.Identifier} - {subObjective.GetType().Name}");
                                }
                            }
                            DebugConsoleNewMessage($"Priority: {gotoObjective.Priority}");
                        }
                    }
                    if (MathUtils.NearlyEqual(lastDmgTime,deltaTime) && gotoObjective != null)
                    {
                        DebugConsoleNewMessage("Free from fire.");
                        gotoObjective.Abandoned -= GoToAbandoned;
                        PathSteering.Reset();
                        RemoveSubObjective(ref gotoObjective);
                        runningAway = false;
                    }
                    if (!runningAway && moveCloser)
                    {
                        if (TryAddSubObjective(
                            ref gotoObjective,
                            () =>
                            {
                                DebugConsole.NewMessage($"{character.Name}: Adding Pathing Objective #{++debugMoveCloser}.");
                                return new AIObjectiveGoTo(sideOfFireTarget, character, objectiveManager, closeEnough: fightingSide == FightingSide.None ? extinguisher.Range * .8f : 0)
                                {
                                    DialogueIdentifier = AIObjectiveGoTo.DialogCannotReachFire,
                                    TargetName = fs.Hull.DisplayName,
                                    AlwaysUseEuclideanDistance = true,
                                };
                            },
                            onAbandon: GoToAbandoned,
                            onCompleted: () => {
                                RemoveSubObjective(ref gotoObjective);
                                DebugConsole.NewMessage($"{character.Name}: Finished navigating closer to fire.");
                            }
                        ))
                        {
                            gotoObjective.requiredCondition = () =>
                            {
                                bool test = character.CanSeeTarget(sideOfFireTarget);
                                return test;
                            };
                        }
                    }
                    else if (!operateExtinguisher || isInDamageRange)
                    {
                        // Don't walk into the flames.
                        RemoveSubObjective(ref gotoObjective);
                        SteeringManager.Reset();
                    }
                    // Only target one fire source at the time.
                    break;
                }
            }
        }

        private void DebugConsoleNewMessage(string message) {
            DebugConsole.NewMessage($"{character.Name}: {message}");
            character.Speak($"d:{message}", Networking.ChatMessageType.Radio);
        }
        private void GoToAbandoned() {
            Abandon = true;
            DebugConsole.NewMessage($"{character.Name}: Path Abandoned. Last going to {sideOfFire} {sideOfFireTarget.WorldPosition}.");
        }

        public override void Reset()
        {
            base.Reset();
            getExtinguisherObjective = null;
            if (gotoObjective != null) {
                gotoObjective.Abandoned -= GoToAbandoned;
                gotoObjective = null;
            }
            sinTime = 0;
            SetWaypoint(targetHull.Position);
            SteeringManager?.Reset();
        }

        protected override void OnCompleted()
        {
            sideOfFireTarget?.Remove();
            base.OnCompleted();
            SteeringManager?.Reset();
        }

        protected override void OnAbandon()
        {
            sideOfFireTarget?.Remove();
            base.OnAbandon();
            SteeringManager?.Reset();
        }
    }
}
