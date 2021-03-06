﻿// 
//     Code From Kerbal Engineer Redux
// 
//     Copyright (C) 2014 CYBUTEK
// 
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// 
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
// 
//     You should have received a copy of the GNU General Public License
//     along with this program.  If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using UnityEngine;

namespace BasicDeltaV.Simulation
{
    public class EngineSim
    {
        public const double GRAVITY = 9.80665;

        private static readonly Pool<EngineSim> pool = new Pool<EngineSim>(Create, Reset);

        private readonly ResourceContainer resourceConsumptions = new ResourceContainer();
        private readonly ResourceContainer maxResourceConsumptions = new ResourceContainer();
        private readonly ResourceContainer resourceFlowModes = new ResourceContainer();

        public double actualThrust = 0;
        public bool isActive = false;
        public double isp = 0;
        public PartSim partSim;
        public List<AppliedForce> appliedForces = new List<AppliedForce>();
        public float maxMach;
        public bool isFlamedOut;
        public bool dontDecoupleActive = true;

        public double thrust = 0;
        public double fullThrust = 0;

        // Add thrust vector to account for directional losses
        public Vector3 thrustVec;

        private static EngineSim Create()
        {
            return new EngineSim();
        }

        private static void Reset(EngineSim engineSim)
        {
            engineSim.resourceConsumptions.Reset();
            engineSim.maxResourceConsumptions.Reset();
            engineSim.resourceFlowModes.Reset();
            engineSim.partSim = null;
            engineSim.actualThrust = 0;
            engineSim.isActive = false;
            engineSim.isp = 0;
            for (int i = 0; i < engineSim.appliedForces.Count; i++)
            {
                engineSim.appliedForces[i].Release();
            }
            engineSim.appliedForces.Clear();
            engineSim.thrust = 0;
            engineSim.fullThrust = 0;
            engineSim.maxMach = 0f;
            engineSim.isFlamedOut = false;
        }

        public void Release()
        {
            pool.Release(this);
        }

        public static EngineSim New(PartSim theEngine,
                                    ModuleEngines engineMod,
                                    double atmosphere,
                                    float machNumber,
                                    bool vectoredThrust,
                                    bool fullThrust,
                                    LogMsg log)
        {
            float maxFuelFlow = engineMod.maxFuelFlow;
            float minFuelFlow = engineMod.minFuelFlow;
            float thrustPercentage = engineMod.thrustPercentage;
            List<Transform> thrustTransforms = engineMod.thrustTransforms;
            List<float> thrustTransformMultipliers = engineMod.thrustTransformMultipliers;
            Vector3 vecThrust = CalculateThrustVector(vectoredThrust ? thrustTransforms : null,
                                                        vectoredThrust ? thrustTransformMultipliers : null,
                                                        log);
            FloatCurve atmosphereCurve = engineMod.atmosphereCurve;
            bool atmChangeFlow = engineMod.atmChangeFlow;
            FloatCurve atmCurve = engineMod.useAtmCurve ? engineMod.atmCurve : null;
            FloatCurve velCurve = engineMod.useVelCurve ? engineMod.velCurve : null;
            float currentThrottle = engineMod.currentThrottle;
            float IspG = engineMod.g;
            bool throttleLocked = engineMod.throttleLocked || fullThrust;
            List<Propellant> propellants = engineMod.propellants;
            bool active = engineMod.isOperational;
            float resultingThrust = engineMod.resultingThrust;
            bool isFlamedOut = engineMod.flameout;

            EngineSim engineSim = pool.Borrow();

            engineSim.isp = 0.0;
            engineSim.maxMach = 0.0f;
            engineSim.actualThrust = 0.0;
            engineSim.partSim = theEngine;
            engineSim.isActive = active;
            engineSim.thrustVec = vecThrust;
            engineSim.isFlamedOut = isFlamedOut;
            engineSim.resourceConsumptions.Reset();
            engineSim.maxResourceConsumptions.Reset();
            engineSim.resourceFlowModes.Reset();
            engineSim.appliedForces.Clear();

            double flowRate = 0.0;
            double maxFlowRate = 0.0;
            if (engineSim.partSim.hasVessel)
            {
                if (log != null) log.AppendLine("hasVessel is true");

                float flowModifier = GetFlowModifier(atmChangeFlow, atmCurve, engineSim.partSim.part.atmDensity, velCurve, machNumber, ref engineSim.maxMach);
                engineSim.isp = atmosphereCurve.Evaluate((float)atmosphere);
                engineSim.fullThrust = GetThrust(maxFuelFlow * flowModifier, engineSim.isp);
                engineSim.thrust = GetThrust(Mathf.Lerp(minFuelFlow, maxFuelFlow, GetThrustPercent(thrustPercentage)) * flowModifier, engineSim.isp);
                engineSim.actualThrust = engineSim.isActive ? resultingThrust : 0.0;
                if (log != null)
                {
                    log.buf.AppendFormat("flowMod = {0:g6}\n", flowModifier);
                    log.buf.AppendFormat("isp     = {0:g6}\n", engineSim.isp);
                    log.buf.AppendFormat("thrust  = {0:g6}\n", engineSim.thrust);
                    log.buf.AppendFormat("actual  = {0:g6}\n", engineSim.actualThrust);
                }

                if (throttleLocked)
                {
                    if (log != null) log.AppendLine("throttleLocked is true, using thrust for flowRate");
                    flowRate = GetFlowRate(engineSim.thrust, engineSim.isp);
                }
                else
                {
                    if (currentThrottle > 0.0f && engineSim.partSim.isLanded == false)
                    {
                        // TODO: This bit doesn't work for RF engines
                        if (log != null) log.AppendLine("throttled up and not landed, using actualThrust for flowRate");
                        flowRate = GetFlowRate(engineSim.actualThrust, engineSim.isp);
                    }
                    else
                    {
                        if (log != null) log.AppendLine("throttled down or landed, using thrust for flowRate");
                        flowRate = GetFlowRate(engineSim.thrust, engineSim.isp);
                    }
                }

                maxFlowRate = GetFlowRate(engineSim.fullThrust, engineSim.isp);
            }
            else
            {
                if (log != null) log.buf.AppendLine("hasVessel is false");
                float altitude = BasicDeltaV.Instance.AtmosphereDepth;
                float flowModifier = GetFlowModifier(atmChangeFlow, atmCurve, BasicDeltaV.Instance.CurrentCelestialBody.GetDensity(BasicDeltaV.Instance.CurrentCelestialBody.GetPressure(altitude), BasicDeltaV.Instance.CurrentCelestialBody.GetTemperature(altitude)), velCurve, machNumber, ref engineSim.maxMach);
                engineSim.isp = atmosphereCurve.Evaluate((float)atmosphere);
                engineSim.fullThrust = GetThrust(maxFuelFlow * flowModifier, engineSim.isp);
                engineSim.thrust = GetThrust(Mathf.Lerp(minFuelFlow, maxFuelFlow, GetThrustPercent(thrustPercentage)) * flowModifier, engineSim.isp);
                engineSim.actualThrust = 0d;
                if (log != null)
                {
                    log.buf.AppendFormat("flowMod = {0:g6}\n", flowModifier);
                    log.buf.AppendFormat("isp     = {0:g6}\n", engineSim.isp);
                    log.buf.AppendFormat("thrust  = {0:g6}\n", engineSim.thrust);
                    log.buf.AppendFormat("actual  = {0:g6}\n", engineSim.actualThrust);
                    log.AppendLine("no vessel, using thrust for flowRate");
                }

                flowRate = GetFlowRate(engineSim.thrust, engineSim.isp);
                maxFlowRate = GetFlowRate(engineSim.fullThrust, engineSim.isp);
            }

            if (log != null) log.buf.AppendFormat("flowRate = {0:g6}\n", flowRate);

            float flowMass = 0f;
            for (int i = 0; i < propellants.Count; ++i)
            {
                Propellant propellant = propellants[i];
                if (!propellant.ignoreForIsp)
                    flowMass += propellant.ratio * ResourceContainer.GetResourceDensity(propellant.id);
            }

            if (log != null) log.buf.AppendFormat("flowMass = {0:g6}\n", flowMass);

            for (int i = 0; i < propellants.Count; ++i)
            {
                Propellant propellant = propellants[i];

                if (propellant.name == "ElectricCharge" || propellant.name == "IntakeAir")
                {
                    continue;
                }

                double consumptionRate = propellant.ratio * flowRate / flowMass;

                if (log != null) log.buf.AppendFormat(
                        "Add consumption({0}, {1}:{2:d}) = {3:g6}\n",
                        ResourceContainer.GetResourceName(propellant.id),
                        theEngine.name,
                        theEngine.partId,
                        consumptionRate);

                engineSim.resourceConsumptions.Add(propellant.id, consumptionRate);
                engineSim.resourceFlowModes.Add(propellant.id, (double)propellant.GetFlowMode());

                double maxConsumptionRate = propellant.ratio * maxFlowRate / flowMass;

                engineSim.maxResourceConsumptions.Add(propellant.id, maxConsumptionRate);
            }

            for (int i = 0; i < thrustTransforms.Count; i++)
            {
                Transform thrustTransform = thrustTransforms[i];
                Vector3d direction = thrustTransform.forward.normalized;
                Vector3d position = thrustTransform.position;

                AppliedForce appliedForce = AppliedForce.New(direction * engineSim.thrust * thrustTransformMultipliers[i], position);
                engineSim.appliedForces.Add(appliedForce);
            }

            return engineSim;
        }

        private static Vector3 CalculateThrustVector(List<Transform> thrustTransforms, List<float> thrustTransformMultipliers, LogMsg log)
        {
            if (thrustTransforms == null)
            {
                return Vector3.forward;
            }

            Vector3 thrustvec = Vector3.zero;
            for (int i = 0; i < thrustTransforms.Count; ++i)
            {
                Transform trans = thrustTransforms[i];

                if (log != null) log.buf.AppendFormat("Transform = ({0:g6}, {1:g6}, {2:g6})   length = {3:g6}\n", trans.forward.x, trans.forward.y, trans.forward.z, trans.forward.magnitude);

                thrustvec -= (trans.forward * thrustTransformMultipliers[i]);
            }

            if (log != null) log.buf.AppendFormat("ThrustVec  = ({0:g6}, {1:g6}, {2:g6})   length = {3:g6}\n", thrustvec.x, thrustvec.y, thrustvec.z, thrustvec.magnitude);

            thrustvec.Normalize();

            if (log != null) log.buf.AppendFormat("ThrustVecN = ({0:g6}, {1:g6}, {2:g6})   length = {3:g6}\n", thrustvec.x, thrustvec.y, thrustvec.z, thrustvec.magnitude);

            return thrustvec;
        }

        public ResourceContainer ResourceConsumptions
        {
            get
            {
                return resourceConsumptions;
            }
        }

        public ResourceContainer MaxResourceConsumptions
        {
            get
            {
                return maxResourceConsumptions;
            }
        }

        public static double GetExhaustVelocity(double isp)
        {
            return isp * GRAVITY;
        }

        public static float GetFlowModifier(bool atmChangeFlow, FloatCurve atmCurve, double atmDensity, FloatCurve velCurve, float machNumber, ref float maxMach)
        {
            float flowModifier = 1.0f;
            if (atmChangeFlow)
            {
                flowModifier = (float)(atmDensity / 1.225);
                if (atmCurve != null)
                {
                    flowModifier = atmCurve.Evaluate(flowModifier);
                }
            }
            if (velCurve != null)
            {
                flowModifier = flowModifier * velCurve.Evaluate(machNumber);
                maxMach = velCurve.maxTime;
            }
            if (flowModifier < float.Epsilon)
            {
                flowModifier = float.Epsilon;
            }
            return flowModifier;
        }

        public static double GetFlowRate(double thrust, double isp)
        {
            return thrust / GetExhaustVelocity(isp);
        }

        public static float GetThrottlePercent(float currentThrottle, float thrustPercentage)
        {
            return currentThrottle * GetThrustPercent(thrustPercentage);
        }

        public static double GetThrust(double flowRate, double isp)
        {
            return flowRate * GetExhaustVelocity(isp);
        }

        public static float GetThrustPercent(float thrustPercentage)
        {
            return thrustPercentage * 0.01f;
        }

        public void DumpEngineToLog(LogMsg log)
        {
            if (log != null) log.buf.AppendFormat("[thrust = {0:g6}, actual = {1:g6}, isp = {2:g6}\n", thrust, actualThrust, isp);
        }

        // A dictionary to hold a set of parts for each resource
        Dictionary<int, HashSet<PartSim>> sourcePartSets = new Dictionary<int, HashSet<PartSim>>();

        Dictionary<int, HashSet<PartSim>> stagePartSets = new Dictionary<int, HashSet<PartSim>>();

        Dictionary<int, HashSet<PartSim>> fullSourcePartSets = new Dictionary<int, HashSet<PartSim>>();

        Dictionary<int, HashSet<PartSim>> fullStagePartSets = new Dictionary<int, HashSet<PartSim>>();

        HashSet<PartSim> visited = new HashSet<PartSim>();

        public void DumpSourcePartSets(LogMsg log, String msg)
        {
            if (log == null)
                return;

            log.AppendLine("DumpSourcePartSets ", msg);
            foreach (int type in sourcePartSets.Keys)
            {
                log.AppendLine("SourcePartSet for ", ResourceContainer.GetResourceName(type));
                HashSet<PartSim> sourcePartSet = sourcePartSets[type];
                if (sourcePartSet.Count > 0)
                {
                    foreach (PartSim partSim in sourcePartSet)
                    {
                        log.AppendLine("Part ", partSim.name, ":", partSim.partId);
                    }
                }
                else
                {
                    log.AppendLine("No parts");
                }
            }
        }

        public bool SetResourceDrains(LogMsg log, List<PartSim> allParts, List<PartSim> allFuelLines, HashSet<PartSim> drainingParts)
        {
            //DumpSourcePartSets(log, "before clear");
            foreach (HashSet<PartSim> sourcePartSet in sourcePartSets.Values)
            {
                sourcePartSet.Clear();
            }
            //DumpSourcePartSets(log, "after clear");

            for (int index = 0; index < this.resourceConsumptions.Types.Count; index++)
            {
                int type = this.resourceConsumptions.Types[index];

                HashSet<PartSim> sourcePartSet;
                if (!sourcePartSets.TryGetValue(type, out sourcePartSet))
                {
                    sourcePartSet = new HashSet<PartSim>();
                    sourcePartSets.Add(type, sourcePartSet);
                }

                switch ((ResourceFlowMode)this.resourceFlowModes[type])
                {
                    case ResourceFlowMode.NO_FLOW:
                        if (partSim.resources[type] > SimManager.RESOURCE_MIN && partSim.resourceFlowStates[type] != 0)
                        {
                            sourcePartSet.Add(partSim);
                        }
                        break;

                    case ResourceFlowMode.ALL_VESSEL:
                    case ResourceFlowMode.ALL_VESSEL_BALANCE:
                        for (int i = 0; i < allParts.Count; i++)
                        {
                            PartSim aPartSim = allParts[i];
                            if (aPartSim.resources[type] > SimManager.RESOURCE_MIN && aPartSim.resourceFlowStates[type] != 0)
                            {
                                sourcePartSet.Add(aPartSim);
                            }
                        }
                        break;

                    case ResourceFlowMode.STAGE_PRIORITY_FLOW:
                    case ResourceFlowMode.STAGE_PRIORITY_FLOW_BALANCE:
                        //All vessel ordered by stage
                        if (log != null) log.Append("Find ", ResourceContainer.GetResourceName(type), " sources for ", partSim.name)
                                            .AppendLine(":", partSim.partId);
                        foreach (HashSet<PartSim> stagePartSet in stagePartSets.Values)
                        {
                            stagePartSet.Clear();
                        }
                        var maxStage = -1;

                        for (int i = 0; i < allParts.Count; i++)
                        {
                            var aPartSim = allParts[i];
                            //if (log != null) log.Append(aPartSim.name, ":" + aPartSim.partId, " contains ", aPartSim.resources[type])
                            //                  .AppendLine((aPartSim.resourceFlowStates[type] == 0) ? " (disabled)" : "");
                            if (aPartSim.resources[type] > SimManager.RESOURCE_MIN && aPartSim.resourceFlowStates[type] != 0)
                            {
                                int stage = aPartSim.inverseStage;
                                if (stage > maxStage)
                                {
                                    maxStage = stage;
                                }

                                HashSet<PartSim> tempPartSet;
                                if (!stagePartSets.TryGetValue(stage, out tempPartSet))
                                {
                                    tempPartSet = new HashSet<PartSim>();
                                    stagePartSets.Add(stage, tempPartSet);
                                }
                                tempPartSet.Add(aPartSim);
                            }
                        }

                        //Add resource containers in order by stage
                        for (int j = maxStage; j >= -1; j--)
                        {
                            //if (log != null) log.AppendLine("Testing stage ", j);
                            HashSet<PartSim> stagePartSet;
                            if (stagePartSets.TryGetValue(j, out stagePartSet) && stagePartSet.Count > 0)
                            {
                                //if (log != null) log.AppendLine("Not empty");
                                // We have to copy the contents of the set here rather than copying the set reference or 
                                // bad things (tm) happen
                                foreach (PartSim aPartSim in stagePartSet)
                                {
                                    sourcePartSet.Add(aPartSim);
                                }
                                break;
                            }
                        }
                        break;

                    case ResourceFlowMode.STACK_PRIORITY_SEARCH:
                    case ResourceFlowMode.STAGE_STACK_FLOW:
                    case ResourceFlowMode.STAGE_STACK_FLOW_BALANCE:
                        visited.Clear();
                        //Resource containers limited to current stage
                        if (log != null) log.Append("Find ", ResourceContainer.GetResourceName(type), " sources for ", partSim.name)
                                            .AppendLine(":", partSim.partId);

                        partSim.GetSourceSet(type, true, allParts, visited, sourcePartSet, false, log, "");
                        break;

                    default:
                        if (log != null) log.Append("SetResourceDrains(", partSim.name, ":", partSim.partId)
                                            .AppendLine(") Unexpected flow type for ", ResourceContainer.GetResourceName(type), ")");
                        break;
                }

                if (log != null && sourcePartSet.Count > 0)
                {
                    log.AppendLine("Source parts for ", ResourceContainer.GetResourceName(type), ":");
                    foreach (PartSim partSim in sourcePartSet)
                    {
                        log.AppendLine(partSim.name, ":", partSim.partId);
                    }
                }

                //DumpSourcePartSets(log, "after " + ResourceContainer.GetResourceName(type));
            }

            // If we don't have sources for all the needed resources then return false without setting up any drains
            for (int i = 0; i < this.resourceConsumptions.Types.Count; i++)
            {
                int type = this.resourceConsumptions.Types[i];
                HashSet<PartSim> sourcePartSet;
                if (!sourcePartSets.TryGetValue(type, out sourcePartSet) || sourcePartSet.Count == 0)
                {
                    if (log != null) log.AppendLine("No source of ", ResourceContainer.GetResourceName(type));
                    isActive = false;
                    return false;
                }
            }

            // Now we set the drains on the members of the sets and update the draining parts set
            for (int i = 0; i < this.resourceConsumptions.Types.Count; i++)
            {
                int type = this.resourceConsumptions.Types[i];
                HashSet<PartSim> sourcePartSet = sourcePartSets[type];
                ResourceFlowMode mode = (ResourceFlowMode)resourceFlowModes[type];
                double consumption = resourceConsumptions[type];
                double amount = 0d;
                double total = 0d;
                if (mode == ResourceFlowMode.ALL_VESSEL_BALANCE ||
                    mode == ResourceFlowMode.STAGE_PRIORITY_FLOW_BALANCE ||
                    mode == ResourceFlowMode.STAGE_STACK_FLOW_BALANCE ||
                    mode == ResourceFlowMode.STACK_PRIORITY_SEARCH)
                {
                    foreach (PartSim partSim in sourcePartSet)
                        total += partSim.resources[type];
                }
                else
                    amount = consumption / sourcePartSet.Count;

                // Loop through the members of the set 
                foreach (PartSim partSim in sourcePartSet)
                {
                    if (total != 0d)
                        amount = consumption * partSim.resources[type] / total;

                    if (log != null) log.Append("Adding drain of ", amount, " ", ResourceContainer.GetResourceName(type))
                                        .AppendLine(" to ", partSim.name, ":", partSim.partId);

                    partSim.resourceDrains.Add(type, amount);
                    drainingParts.Add(partSim);
                }
            }
            return true;
        }

        public bool SetPossibleResourceDrains(LogMsg log, List<PartSim> allParts, HashSet<PartSim> drainingParts)
        {
            //DumpSourcePartSets(log, "before clear");
            foreach (HashSet<PartSim> sourcePartSet in fullSourcePartSets.Values)
            {
                sourcePartSet.Clear();
            }
            //DumpSourcePartSets(log, "after clear");

            for (int index = 0; index < this.resourceConsumptions.Types.Count; index++)
            {
                int type = this.resourceConsumptions.Types[index];
                                
                if (!fullSourcePartSets.TryGetValue(type, out HashSet<PartSim> sourcePartSet))
                {
                    sourcePartSet = new HashSet<PartSim>();
                    fullSourcePartSets.Add(type, sourcePartSet);
                }

                switch ((ResourceFlowMode)this.resourceFlowModes[type])
                {
                    case ResourceFlowMode.NO_FLOW:
                        if (partSim.maxResources[type] > SimManager.RESOURCE_MIN)
                        {
                            sourcePartSet.Add(partSim);
                        }
                        break;

                    case ResourceFlowMode.ALL_VESSEL:
                    case ResourceFlowMode.ALL_VESSEL_BALANCE:
                        for (int i = 0; i < allParts.Count; i++)
                        {
                            PartSim aPartSim = allParts[i];
                            if (aPartSim.maxResources[type] > SimManager.RESOURCE_MIN)
                            {
                                sourcePartSet.Add(aPartSim);
                            }
                        }
                        break;

                    case ResourceFlowMode.STAGE_PRIORITY_FLOW:
                    case ResourceFlowMode.STAGE_PRIORITY_FLOW_BALANCE:
                        //All vessel ordered by stage
                        if (log != null) log.Append("Find ", ResourceContainer.GetResourceName(type), " sources for ", partSim.name)
                                            .AppendLine(":", partSim.partId);
                        foreach (HashSet<PartSim> stagePartSet in stagePartSets.Values)
                        {
                            stagePartSet.Clear();
                        }
                        var maxStage = -1;

                        for (int i = 0; i < allParts.Count; i++)
                        {
                            var aPartSim = allParts[i];
                            //if (log != null) log.Append(aPartSim.name, ":" + aPartSim.partId, " contains ", aPartSim.resources[type])
                            //                  .AppendLine((aPartSim.resourceFlowStates[type] == 0) ? " (disabled)" : "");
                            if (aPartSim.maxResources[type] > SimManager.RESOURCE_MIN)
                            {
                                int stage = aPartSim.inverseStage;
                                if (stage > maxStage)
                                {
                                    maxStage = stage;
                                }
                                                                
                                if (!stagePartSets.TryGetValue(stage, out HashSet<PartSim> tempPartSet))
                                {
                                    tempPartSet = new HashSet<PartSim>();
                                    stagePartSets.Add(stage, tempPartSet);
                                }
                                tempPartSet.Add(aPartSim);
                            }
                        }

                        //Add resource containers in order by stage
                        for (int j = maxStage; j >= -1; j--)
                        {
                            //if (log != null) log.AppendLine("Testing stage ", j);

                            if (stagePartSets.TryGetValue(j, out HashSet<PartSim> stagePartSet) && stagePartSet.Count > 0)
                            {
                                //if (log != null) log.AppendLine("Not empty");
                                // We have to copy the contents of the set here rather than copying the set reference or 
                                // bad things (tm) happen
                                foreach (PartSim aPartSim in stagePartSet)
                                {
                                    sourcePartSet.Add(aPartSim);
                                }
                                break;
                            }
                        }
                        break;

                    case ResourceFlowMode.STACK_PRIORITY_SEARCH:
                    case ResourceFlowMode.STAGE_STACK_FLOW:
                    case ResourceFlowMode.STAGE_STACK_FLOW_BALANCE:
                        visited.Clear();
                        //Resource containers limited to current stage
                        if (log != null) log.Append("Find ", ResourceContainer.GetResourceName(type), " sources for ", partSim.name)
                                            .AppendLine(":", partSim.partId);

                        partSim.GetSourceSet(type, true, allParts, visited, sourcePartSet, true, log, "");
                        break;

                    default:
                        if (log != null) log.Append("SetResourceDrains(", partSim.name, ":", partSim.partId)
                                            .AppendLine(") Unexpected flow type for ", ResourceContainer.GetResourceName(type), ")");
                        break;
                }
            }

            // If we don't have sources for all the needed resources then return false without setting up any drains
            for (int i = 0; i < this.resourceConsumptions.Types.Count; i++)
            {
                int type = this.resourceConsumptions.Types[i];
                
                if (!fullSourcePartSets.TryGetValue(type, out HashSet<PartSim> sourcePartSet) || sourcePartSet.Count == 0)
                {
                    if (log != null) log.AppendLine("No source of ", ResourceContainer.GetResourceName(type));

                    return false;
                }
            }

            // Now we set the drains on the members of the sets and update the draining parts set
            for (int i = 0; i < this.resourceConsumptions.Types.Count; i++)
            {
                HashSet<PartSim> sourcePartSet = fullSourcePartSets[resourceConsumptions.Types[i]];

                // Loop through the members of the set 
                foreach (PartSim partSim in sourcePartSet)
                {
                    drainingParts.Add(partSim);
                }
            }

            return true;
        }

    }
}
