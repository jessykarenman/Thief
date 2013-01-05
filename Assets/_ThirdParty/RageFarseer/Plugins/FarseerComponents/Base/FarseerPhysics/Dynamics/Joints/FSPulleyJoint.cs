/*
* Farseer Physics Engine based on Box2D.XNA port:
* Copyright (c) 2011 Ian Qvist
* 
* Original source Box2D:
* Copyright (c) 2006-2011 Erin Catto http://www.box2d.org 
* 
* This software is provided 'as-is', without any express or implied 
* warranty.  In no event will the authors be held liable for any damages 
* arising from the use of this software. 
* Permission is granted to anyone to use this software for any purpose, 
* including commercial applications, and to alter it and redistribute it 
* freely, subject to the following restrictions: 
* 1. The origin of this software must not be misrepresented; you must not 
* claim that you wrote the original software. If you use this software 
* in a product, an acknowledgment in the product documentation would be 
* appreciated but is not required. 
* 2. Altered source versions must be plainly marked as such, and must not be 
* misrepresented as being the original software. 
* 3. This notice may not be removed or altered from any source distribution. 
*/

using System;
using System.Diagnostics;
using FarseerPhysics.Common;
using Microsoft.Xna.Framework;

namespace FarseerPhysics.Dynamics.Joints
{
    // Pulley:
    // length1 = norm(p1 - s1)
    // length2 = norm(p2 - s2)
    // C0 = (length1 + ratio * length2)_initial
    // C = C0 - (length1 + ratio * length2)
    // u1 = (p1 - s1) / norm(p1 - s1)
    // u2 = (p2 - s2) / norm(p2 - s2)
    // Cdot = -dot(u1, v1 + cross(w1, r1)) - ratio * dot(u2, v2 + cross(w2, r2))
    // J = -[u1 cross(r1, u1) ratio * u2  ratio * cross(r2, u2)]
    // K = J * invM * JT
    //   = invMass1 + invI1 * cross(r1, u1)^2 + ratio^2 * (invMass2 + invI2 * cross(r2, u2)^2)

    /// <summary>
    /// The pulley joint is connected to two bodies and two fixed ground points.
    /// The pulley supports a ratio such that:
    /// length1 + ratio * length2 <= constant
    /// Yes, the force transmitted is scaled by the ratio.
    /// Warning: the pulley joint can get a bit squirrelly by itself. They often
    /// work better when combined with prismatic joints. You should also cover the
    /// the anchor points with static shapes to prevent one side from going to
    /// zero length.
    /// </summary>
    public class FSPulleyJoint : FarseerJoint
    {
        /// <summary>
        /// Get the first ground anchor.
        /// </summary>
        /// <value></value>
        public FVector2 GroundAnchorA;

        /// <summary>
        /// Get the second ground anchor.
        /// </summary>
        /// <value></value>
        public FVector2 GroundAnchorB;

        // Solver shared
        public FVector2 LocalAnchorA;
        public FVector2 LocalAnchorB;

        private float _impulse;
        protected float _limitImpulse1 = 0f;
        protected float _limitImpulse2 = 0f;
        private float m_constant;

        // Solver temp
        private int m_indexA;
        private int m_indexB;
        private FVector2 m_uA;
        private FVector2 m_uB;
        private FVector2 m_rA;
        private FVector2 m_rB;
        private FVector2 m_localCenterA;
        private FVector2 m_localCenterB;
        private float m_invMassA;
        private float m_invMassB;
        private float m_invIA;
        private float m_invIB;
        private float m_mass;

        internal FSPulleyJoint()
        {
            JointType = JointType.Pulley;
        }

        /// <summary>
        /// Initialize the bodies, anchors, lengths, max lengths, and ratio using the world anchors.
        /// This requires two ground anchors,
        /// two dynamic body anchor points, max lengths for each side,
        /// and a pulley ratio.
        /// </summary>
        /// <param name="bA">The first body.</param>
        /// <param name="bB">The second body.</param>
        /// <param name="groundA">The ground anchor for the first body.</param>
        /// <param name="groundB">The ground anchor for the second body.</param>
        /// <param name="anchorA">The first body anchor.</param>
        /// <param name="anchorB">The second body anchor.</param>
        /// <param name="ratio">The ratio.</param>
        public FSPulleyJoint(FSBody bA, FSBody bB, FVector2 groundA, FVector2 groundB, FVector2 anchorA, FVector2 anchorB, float ratio)
            : base(bA, bB)
        {
            JointType = JointType.Pulley;

            GroundAnchorA = groundA;
            GroundAnchorB = groundB;
            LocalAnchorA = anchorA;
            LocalAnchorB = anchorB;

            Debug.Assert(ratio != 0.0f);
            Debug.Assert(ratio > FSSettings.Epsilon);

            Ratio = ratio;

            FVector2 dA = BodyA.GetWorldPoint(anchorA) - groundA;
            LengthA = dA.Length();

            FVector2 dB = BodyB.GetWorldPoint(anchorB) - groundB;
            LengthB = dB.Length();

            m_constant = LengthA + ratio * LengthB;

            _impulse = 0.0f;
        }

        public override FVector2 WorldAnchorA
        {
            get { return BodyA.GetWorldPoint(LocalAnchorA); }
        }

        public override FVector2 WorldAnchorB
        {
            get { return BodyB.GetWorldPoint(LocalAnchorB); }
            set { Debug.Assert(false, "You can't set the world anchor on this joint type."); }
        }

        /// <summary>
        /// Get the current length of the segment attached to body1.
        /// </summary>
        /// <value></value>
        public float LengthA { get; set; }

        /// <summary>
        /// Get the current length of the segment attached to body2.
        /// </summary>
        /// <value></value>
        public float LengthB { get; set; }

        public float CurrentLengthA
        {
            get
            {
                FVector2 p = BodyA.GetWorldPoint(LocalAnchorA);
                FVector2 s = GroundAnchorA;
                FVector2 d = p - s;
                return d.Length();
            }
        }

        public float CurrentLengthB
        {
            get
            {
                FVector2 p = BodyB.GetWorldPoint(LocalAnchorB);
                FVector2 s = GroundAnchorB;
                FVector2 d = p - s;
                return d.Length();
            }
        }

        /// <summary>
        /// Get the pulley ratio.
        /// </summary>
        /// <value></value>
        public float Ratio { get; set; }

        public override FVector2 GetReactionForce(float inv_dt)
        {
            FVector2 P = _impulse * m_uB;
            return inv_dt * P;
        }

        public override float GetReactionTorque(float inv_dt)
        {
            return 0.0f;
        }

        internal override void InitVelocityConstraints(ref SolverData data)
        {
            m_indexA = BodyA.IslandIndex;
            m_indexB = BodyB.IslandIndex;
            m_localCenterA = BodyA.Sweep.LocalCenter;
            m_localCenterB = BodyB.Sweep.LocalCenter;
            m_invMassA = BodyA.InvMass;
            m_invMassB = BodyB.InvMass;
            m_invIA = BodyA.InvI;
            m_invIB = BodyB.InvI;

            FVector2 cA = data.positions[m_indexA].c;
            float aA = data.positions[m_indexA].a;
            FVector2 vA = data.velocities[m_indexA].v;
            float wA = data.velocities[m_indexA].w;

            FVector2 cB = data.positions[m_indexB].c;
            float aB = data.positions[m_indexB].a;
            FVector2 vB = data.velocities[m_indexB].v;
            float wB = data.velocities[m_indexB].w;

            Rot qA = new Rot(aA), qB = new Rot(aB);

            m_rA = MathUtils.Mul(qA, LocalAnchorA - m_localCenterA);
            m_rB = MathUtils.Mul(qB, LocalAnchorB - m_localCenterB);

            // Get the pulley axes.
            m_uA = cA + m_rA - GroundAnchorA;
            m_uB = cB + m_rB - GroundAnchorB;

            float lengthA = m_uA.Length();
            float lengthB = m_uB.Length();

            if (lengthA > 10.0f * FSSettings.LinearSlop)
            {
                m_uA *= 1.0f / lengthA;
            }
            else
            {
                m_uA = FVector2.Zero;
            }

            if (lengthB > 10.0f * FSSettings.LinearSlop)
            {
                m_uB *= 1.0f / lengthB;
            }
            else
            {
                m_uB = FVector2.Zero;
            }

            // Compute effective mass.
            float ruA = MathUtils.Cross(m_rA, m_uA);
            float ruB = MathUtils.Cross(m_rB, m_uB);

            float mA = m_invMassA + m_invIA * ruA * ruA;
            float mB = m_invMassB + m_invIB * ruB * ruB;

            m_mass = mA + Ratio * Ratio * mB;

            if (m_mass > 0.0f)
            {
                m_mass = 1.0f / m_mass;
            }

            if (FSSettings.EnableWarmstarting)
            {
                // Scale impulses to support variable time steps.
                _impulse *= data.step.dtRatio;

                // Warm starting.
                FVector2 PA = -(_impulse + _limitImpulse1) * m_uA;
                FVector2 PB = (-Ratio * _impulse - _limitImpulse2) * m_uB;
                vA += m_invMassA * PA;
                wA += m_invIA * MathUtils.Cross(m_rA, PA);
                vB += m_invMassB * PB;
                wB += m_invIB * MathUtils.Cross(m_rB, PB);
            }
            else
            {
                _impulse = 0.0f;
            }

            data.velocities[m_indexA].v = vA;
            data.velocities[m_indexA].w = wA;
            data.velocities[m_indexB].v = vB;
            data.velocities[m_indexB].w = wB;

        }

        internal override void SolveVelocityConstraints(ref SolverData data)
        {
            FVector2 vA = data.velocities[m_indexA].v;
            float wA = data.velocities[m_indexA].w;
            FVector2 vB = data.velocities[m_indexB].v;
            float wB = data.velocities[m_indexB].w;

            FVector2 vpA = vA + MathUtils.Cross(wA, m_rA);
            FVector2 vpB = vB + MathUtils.Cross(wB, m_rB);

            float Cdot = -FVector2.Dot(m_uA, vpA) - Ratio * FVector2.Dot(m_uB, vpB);
            float impulse = -m_mass * Cdot;
            _impulse += impulse;

            FVector2 PA = -impulse * m_uA;
            FVector2 PB = -Ratio * impulse * m_uB;
            vA += m_invMassA * PA;
            wA += m_invIA * MathUtils.Cross(m_rA, PA);
            vB += m_invMassB * PB;
            wB += m_invIB * MathUtils.Cross(m_rB, PB);

            data.velocities[m_indexA].v = vA;
            data.velocities[m_indexA].w = wA;
            data.velocities[m_indexB].v = vB;
            data.velocities[m_indexB].w = wB;
        }

        internal override bool SolvePositionConstraints(ref SolverData data)
        {
            FVector2 cA = data.positions[m_indexA].c;
            float aA = data.positions[m_indexA].a;
            FVector2 cB = data.positions[m_indexB].c;
            float aB = data.positions[m_indexB].a;

            Rot qA = new Rot(aA), qB = new Rot(aB);

            FVector2 rA = MathUtils.Mul(qA, LocalAnchorA - m_localCenterA);
            FVector2 rB = MathUtils.Mul(qB, LocalAnchorB - m_localCenterB);

            // Get the pulley axes.
            FVector2 uA = cA + rA - GroundAnchorA;
            FVector2 uB = cB + rB - GroundAnchorB;

            float lengthA = uA.Length();
            float lengthB = uB.Length();

            if (lengthA > 10.0f * FSSettings.LinearSlop)
            {
                uA *= 1.0f / lengthA;
            }
            else
            {
                uA = FVector2.Zero;
            }

            if (lengthB > 10.0f * FSSettings.LinearSlop)
            {
                uB *= 1.0f / lengthB;
            }
            else
            {
                uB = FVector2.Zero;
            }

            // Compute effective mass.
            float ruA = MathUtils.Cross(rA, uA);
            float ruB = MathUtils.Cross(rB, uB);

            float mA = m_invMassA + m_invIA * ruA * ruA;
            float mB = m_invMassB + m_invIB * ruB * ruB;

            float mass = mA + Ratio * Ratio * mB;

            if (mass > 0.0f)
            {
                mass = 1.0f / mass;
            }

            float C = m_constant - lengthA - Ratio * lengthB;
            float linearError = Math.Abs(C);

            float impulse = -mass * C;

            FVector2 PA = -impulse * uA;
            FVector2 PB = -Ratio * impulse * uB;

            cA += m_invMassA * PA;
            aA += m_invIA * MathUtils.Cross(rA, PA);
            cB += m_invMassB * PB;
            aB += m_invIB * MathUtils.Cross(rB, PB);

            data.positions[m_indexA].c = cA;
            data.positions[m_indexA].a = aA;
            data.positions[m_indexB].c = cB;
            data.positions[m_indexB].a = aB;

            return linearError < FSSettings.LinearSlop;
        }
    }
}