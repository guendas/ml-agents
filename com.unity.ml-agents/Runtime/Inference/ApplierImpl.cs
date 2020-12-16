using System;
using System.Collections.Generic;
using System.Linq;
using Unity.MLAgents.Inference.Utils;
using Unity.MLAgents.Actuators;
using Unity.Barracuda;
using UnityEngine;

namespace Unity.MLAgents.Inference
{
    /// <summary>
    /// The Applier for the Continuous Action output tensor. Tensor is assumed to contain the
    /// continuous action data of the agents in the batch.
    /// </summary>
    internal class ContinuousActionOutputApplier : TensorApplier.IApplier
    {
        readonly ActionSpec m_ActionSpec;

        public ContinuousActionOutputApplier(ActionSpec actionSpec)
        {
            m_ActionSpec = actionSpec;
        }

        public void Apply(TensorProxy tensorProxy, IEnumerable<int> actionIds, Dictionary<int, ActionBuffers> lastActions)
        {
            var actionSize = tensorProxy.shape[tensorProxy.shape.Length - 1];
            var agentIndex = 0;
            foreach (int agentId in actionIds)
            {
                if (lastActions.ContainsKey(agentId))
                {
                    var actionBuffer = lastActions[agentId];
                    if (actionBuffer.IsEmpty())
                    {
                        actionBuffer = new ActionBuffers(m_ActionSpec);
                        lastActions[agentId] = actionBuffer;
                    }
                    var continuousBuffer = actionBuffer.ContinuousActions;
                    for (var j = 0; j < actionSize; j++)
                    {
                        continuousBuffer[j] = tensorProxy.data[agentIndex, j];
                    }
                }
                agentIndex++;
            }
        }
    }

    /// <summary>
    /// The Applier for the Discrete Action output tensor. Tensor is assumed to contain the
    /// sampled discrete action data of the agents in the batch.
    /// </summary>
    internal class DiscreteActionOutputApplier : TensorApplier.IApplier
    {
        readonly ActionSpec m_ActionSpec;

        public DiscreteActionOutputApplier(ActionSpec actionSpec)
        {
            m_ActionSpec = actionSpec;
        }

        public void Apply(TensorProxy tensorProxy, IEnumerable<int> actionIds, Dictionary<int, ActionBuffers> lastActions)
        {
            var actionSize = tensorProxy.shape[tensorProxy.shape.Length - 1];
            var agentIndex = 0;
            foreach (int agentId in actionIds)
            {
                if (lastActions.ContainsKey(agentId))
                {
                    var actionBuffer = lastActions[agentId];
                    if (actionBuffer.IsEmpty())
                    {
                        actionBuffer = new ActionBuffers(m_ActionSpec);
                        lastActions[agentId] = actionBuffer;
                    }
                    var discreteBuffer = actionBuffer.DiscreteActions;
                    for (var j = 0; j < actionSize; j++)
                    {
                        discreteBuffer[j] = (int)tensorProxy.data[agentIndex, j];
                    }
                }
                agentIndex++;
            }
        }
    }

    /// <summary>
    /// The Applier for the Discrete Action Probabilities output tensor. Uses
    /// multinomial to sample discrete actions from the logits contained in the tensor.
    /// </summary>
    internal class DiscreteActionProbsOutputApplier : TensorApplier.IApplier
    {
        readonly int[] m_ActionSize;
        readonly Multinomial m_Multinomial;
        readonly ITensorAllocator m_Allocator;
        readonly ActionSpec m_ActionSpec;

        public DiscreteActionProbsOutputApplier(ActionSpec actionSpec, int seed, ITensorAllocator allocator)
        {
            m_ActionSize = actionSpec.BranchSizes;
            m_Multinomial = new Multinomial(seed);
            m_Allocator = allocator;
            m_ActionSpec = actionSpec;
        }

        public void Apply(TensorProxy tensorProxy, IEnumerable<int> actionIds, Dictionary<int, ActionBuffers> lastActions)
        {
            //var tensorDataProbabilities = tensorProxy.Data as float[,];
            var idActionPairList = actionIds as List<int> ?? actionIds.ToList();
            var batchSize = idActionPairList.Count;
            var actionValues = new float[batchSize, m_ActionSize.Length];
            var startActionIndices = Utilities.CumSum(m_ActionSize);
            for (var actionIndex = 0; actionIndex < m_ActionSize.Length; actionIndex++)
            {
                var nBranchAction = m_ActionSize[actionIndex];
                var actionProbs = new TensorProxy()
                {
                    valueType = TensorProxy.TensorType.FloatingPoint,
                    shape = new long[] { batchSize, nBranchAction },
                    data = m_Allocator.Alloc(new TensorShape(batchSize, nBranchAction))
                };

                for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
                {
                    for (var branchActionIndex = 0;
                         branchActionIndex < nBranchAction;
                         branchActionIndex++)
                    {
                        actionProbs.data[batchIndex, branchActionIndex] =
                            tensorProxy.data[batchIndex, startActionIndices[actionIndex] + branchActionIndex];
                    }
                }

                var outputTensor = new TensorProxy()
                {
                    valueType = TensorProxy.TensorType.FloatingPoint,
                    shape = new long[] { batchSize, 1 },
                    data = m_Allocator.Alloc(new TensorShape(batchSize, 1))
                };

                Eval(actionProbs, outputTensor, m_Multinomial);

                for (var ii = 0; ii < batchSize; ii++)
                {
                    actionValues[ii, actionIndex] = outputTensor.data[ii, 0];
                }
                actionProbs.data.Dispose();
                outputTensor.data.Dispose();
            }
            var agentIndex = 0;
            foreach (int agentId in actionIds)
            {
                if (lastActions.ContainsKey(agentId))
                {
                    var actionBuffer = lastActions[agentId];
                    if (actionBuffer.IsEmpty())
                    {
                        actionBuffer = new ActionBuffers(m_ActionSpec);
                        lastActions[agentId] = actionBuffer;
                    }
                    var discreteBuffer = actionBuffer.DiscreteActions;
                    for (var j = 0; j < m_ActionSize.Length; j++)
                    {
                        discreteBuffer[j] = (int)actionValues[agentIndex, j];
                    }
                }
                agentIndex++;
            }
        }

        /// <summary>
        /// Draw samples from a multinomial distribution based on log-probabilities specified
        /// in tensor src. The samples will be saved in the dst tensor.
        /// </summary>
        /// <param name="src">2-D tensor with shape batch_size x num_classes</param>
        /// <param name="dst">Allocated tensor with size batch_size x num_samples</param>
        /// <param name="multinomial">Multinomial object used to sample values</param>
        /// <exception cref="NotImplementedException">
        /// Multinomial doesn't support integer tensors
        /// </exception>
        /// <exception cref="ArgumentException">Issue with tensor shape or type</exception>
        /// <exception cref="ArgumentNullException">
        /// At least one of the tensors is not allocated
        /// </exception>
        public static void Eval(TensorProxy src, TensorProxy dst, Multinomial multinomial)
        {
            if (src.DataType != typeof(float))
            {
                throw new NotImplementedException("Only float tensors are currently supported");
            }

            if (src.valueType != dst.valueType)
            {
                throw new ArgumentException(
                    "Source and destination tensors have different types!");
            }

            if (src.data == null || dst.data == null)
            {
                throw new ArgumentNullException();
            }

            if (src.data.batch != dst.data.batch)
            {
                throw new ArgumentException("Batch size for input and output data is different!");
            }

            var cdf = new float[src.data.channels];

            for (var batch = 0; batch < src.data.batch; ++batch)
            {
                // Find the class maximum
                var maxProb = float.NegativeInfinity;
                for (var cls = 0; cls < src.data.channels; ++cls)
                {
                    maxProb = Mathf.Max(src.data[batch, cls], maxProb);
                }

                // Sum the log probabilities and compute CDF
                var sumProb = 0.0f;
                for (var cls = 0; cls < src.data.channels; ++cls)
                {
                    sumProb += Mathf.Exp(src.data[batch, cls] - maxProb);
                    cdf[cls] = sumProb;
                }

                // Generate the samples
                for (var sample = 0; sample < dst.data.channels; ++sample)
                {
                    dst.data[batch, sample] = multinomial.Sample(cdf);
                }
            }
        }
    }

    /// <summary>
    /// The Applier for the Memory output tensor. Tensor is assumed to contain the new
    /// memory data of the agents in the batch.
    /// </summary>
    internal class MemoryOutputApplier : TensorApplier.IApplier
    {
        Dictionary<int, List<float>> m_Memories;

        public MemoryOutputApplier(
            Dictionary<int, List<float>> memories)
        {
            m_Memories = memories;
        }

        public void Apply(TensorProxy tensorProxy, IEnumerable<int> actionIds, Dictionary<int, ActionBuffers> lastActions)
        {
            var agentIndex = 0;
            var memorySize = (int)tensorProxy.shape[tensorProxy.shape.Length - 1];
            foreach (int agentId in actionIds)
            {
                List<float> memory;
                if (!m_Memories.TryGetValue(agentId, out memory)
                    || memory.Count < memorySize)
                {
                    memory = new List<float>();
                    memory.AddRange(Enumerable.Repeat(0f, memorySize));
                }

                m_Memories[agentId] = memory;
                agentIndex++;
            }
        }
    }

    internal class BarracudaMemoryOutputApplier : TensorApplier.IApplier
    {
        readonly int m_MemoriesCount;
        readonly int m_MemoryIndex;

        Dictionary<int, List<float>> m_Memories;

        public BarracudaMemoryOutputApplier(
            int memoriesCount,
            int memoryIndex,
            Dictionary<int, List<float>> memories)
        {
            m_MemoriesCount = memoriesCount;
            m_MemoryIndex = memoryIndex;
            m_Memories = memories;
        }

        public void Apply(TensorProxy tensorProxy, IEnumerable<int> actionIds, Dictionary<int, ActionBuffers> lastActions)
        {
            var agentIndex = 0;
            var memorySize = (int)tensorProxy.shape[tensorProxy.shape.Length - 1];

            foreach (int agentId in actionIds)
            {
                List<float> memory;
                if (!m_Memories.TryGetValue(agentId, out memory)
                    || memory.Count < memorySize * m_MemoriesCount)
                {
                    memory = new List<float>();
                    memory.AddRange(Enumerable.Repeat(0f, memorySize * m_MemoriesCount));
                }

                for (var j = 0; j < memorySize; j++)
                {
                    memory[memorySize * m_MemoryIndex + j] = tensorProxy.data[agentIndex, j];
                }

                m_Memories[agentId] = memory;
                agentIndex++;
            }
        }
    }
}
