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
        private FightingSide fightingSide = FightingSide.None;
        private WayPoint sideOfFire;

        public AIObjectiveExtinguishFire(Character character, Hull targetHull, AIObjectiveManager objectiveManager, float priorityModifier = 1) 
            : base(character, objectiveManager, priorityModifier)
        {
            this.targetHull = targetHull;
            SetWaypoint(targetHull.Position + new Vector2(targetHull.Size.X / 2f, -targetHull.Size.Y / 2f));    
        }

        private void SetWaypoint(Vector2 worldPos)
        {
            sideOfFire = new WayPoint(worldPos, SpawnType.Disabled, targetHull.Submarine);
        }

        protected override float GetPriority()
        {
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
        protected override void Act(float deltaTime)
        {
            lastDmgTime += deltaTime;
            var extinguisherItem = character.Inventory.FindItemByTag(Tags.FireExtinguisher);
            if (extinguisherItem == null || extinguisherItem.Condition <= 0.0f || !character.HasEquippedItem(extinguisherItem))
            {
                TryAddSubObjective(ref getExtinguisherObjective, () =>
                {
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
                        sideOfFire = new WayPoint(fs.Position, SpawnType.Disabled, targetHull.Submarine);
                        DebugConsole.NewMessage($"Fighting Left");
                    }
                    else if (fightingSide == FightingSide.Right)
                    {
                        sideOfFire = new WayPoint(fs.Position + new Vector2(fs.Size.X, 0), SpawnType.Disabled, targetHull.Submarine);
                        DebugConsole.NewMessage($"Fighting Right");
                    }

                    float xDist = Math.Abs(character.WorldPosition.X - ((fightingSide != FightingSide.None) ? (sideOfFire.WorldPosition.X) : (fs.WorldPosition.X + fs.Size.X / 2f) ) );
                    // Log the character worldposx, log if we are using side of fire as a waypoint, and also the fire source worldposx and the size divided by 2
                    DebugConsole.NewMessage($"{character.Name}: Character WorldPosX: {character.WorldPosition.X} SideOfFire: {(fightingSide != FightingSide.None ? sideOfFire.WorldPosition.X : fs.WorldPosition.X + fs.Size.X / 2f)} FireSource: {fs.WorldPosition.X + fs.Size.X / 2f} Size: {fs.Size.X / 2f} XDist: {xDist}");
                    // If fire source and the character are on the same level, it's better to ignore the y-axis (e.g. it doesn't matter if we stand or crouch), as the fire size is rectangular.
                    // If we'd do this while climbing, the character would often get too close to the fire.
                    ;
                    float yDist = !character.IsClimbing && MathUtils.NearlyEqual(
                        character.WorldPosition.Y - ConvertUnits.ToDisplayUnits(character.AnimController.ColliderHeightFromFloor),
                        targetHull.WorldPosition.Y - targetHull.Size.Y
                    ) ? 0.0f : Math.Abs(
                        character.WorldPosition.Y
                        - ConvertUnits.ToDisplayUnits(character.AnimController.ColliderHeightFromFloor)
                        - (fightingSide != FightingSide.None ? sideOfFire.WorldPosition.Y : fs.WorldPosition.Y)
                    );
                    float distSqr = xDist*xDist + yDist*yDist;
                    DebugConsole.NewMessage($"{character.Name}: Dist Sqr: {distSqr} ({xDist}, {yDist})");
                    bool inRange = distSqr < extinguisher.Range * extinguisher.Range;
                    bool isInDamageRange = fs.IsInDamageRange(character, fs.DamageRange) && character.CanSeeTarget(targetHull);
                    if (isInDamageRange) lastDmgTime = 0;
                    DebugConsole.NewMessage($"{character.Name}: {(character.CanSeeTarget(sideOfFire) ? "Can see fire!!!" : "Can't see fire yet...")}");
                    DebugConsole.NewMessage($"{character.Name}: {(inRange ? "In range" : "Not in range")}");
                    DebugConsole.NewMessage($"{character.Name}: {(isInDamageRange ? "I am in dmg range" : "I am not in dmg range")}");
                    bool moveCloser = !isInDamageRange && (!inRange || !character.CanSeeTarget(sideOfFire)) && lastDmgTime > dmgDelayMove;
                    bool operateExtinguisher = !moveCloser || (inRange && character.CanSeeTarget(sideOfFire));
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
                    if (moveCloser)
                    {
                        if (fightingSide == FightingSide.Left)
                        {
                            sideOfFire = new WayPoint(fs.Position, SpawnType.Disabled, targetHull.Submarine);
                            DebugConsole.NewMessage($"{character.Name}: Fighting Left");
                        } else if (fightingSide == FightingSide.Right)
                        {
                            sideOfFire = new WayPoint(fs.Position + new Vector2(fs.Size.X, 0), SpawnType.Disabled, targetHull.Submarine);
                            DebugConsole.NewMessage($"{character.Name}: Fighting Right");
                        }

                        DebugConsole.NewMessage($"{character.Name}: Adding Pathing Objective #{++debugMoveCloser}.");
                        if (TryAddSubObjective(
                            ref gotoObjective,
                            () => new AIObjectiveGoTo(sideOfFire, character, objectiveManager, closeEnough: fightingSide == FightingSide.None ? extinguisher.Range * .8f : 0)
                            {
                                DialogueIdentifier = AIObjectiveGoTo.DialogCannotReachFire,
                                TargetName = fs.Hull.DisplayName,
                                AlwaysUseEuclideanDistance = true,
                            },
                            onAbandon: () => {
                                Abandon = true;
                                // debug console that sideoffire is not null
                                DebugConsole.NewMessage($"{character.Name}: Abandoning WP:{sideOfFire.WorldPosition}, IgnoreUnsafe:{gotoObjective.IgnoreUnsafeHulls}");
                            },
                            onCompleted: () => {
                                RemoveSubObjective(ref gotoObjective);
                                DebugConsole.NewMessage($"{character.Name}: Completed");
                            }
                        ))
                        {
                            gotoObjective.requiredCondition = () =>
                            {
                                bool test = character.CanSeeTarget(sideOfFire);
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

        public override void Reset()
        {
            base.Reset();
            getExtinguisherObjective = null;
            gotoObjective = null;
            sinTime = 0;
            SteeringManager?.Reset();
        }

        protected override void OnCompleted()
        {
            base.OnCompleted();
            SteeringManager?.Reset();
        }

        protected override void OnAbandon()
        {
            base.OnAbandon();
            SteeringManager?.Reset();
        }
    }
}
