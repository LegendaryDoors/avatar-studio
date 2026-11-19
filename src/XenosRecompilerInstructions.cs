// XenosRecomp's HLSL recompiler (hedge-dev/XenosRecomp 990d03b, shader_recompiler.cpp), ported to C#.
// Copyright (c) 2025 hedge-dev and contributors, MIT (XenosRecomp.LICENSE).
// This file: the fetch and ALU instructions (printDstSwizzle to recompile(AluInstruction)).

using System;
using System.Globalization;

namespace AvatarStudio
{
    sealed partial class XenosRecompiler
    {
        private void printDstSwizzle(uint dstSwizzle, bool operand)
        {
            for (uint i = 0; i < 4; i++)
            {
                FetchDestinationSwizzle swizzle = getDestSwizzle(dstSwizzle, i);
                if (swizzle >= FetchDestinationSwizzle.X && swizzle <= FetchDestinationSwizzle.W)
                    @out.Append(SWIZZLES[operand ? (uint)swizzle : i]);
            }
        }

        private void printDstSwizzle01(uint dstRegister, uint dstSwizzle)
        {
            for (uint i = 0; i < 4; i++)
            {
                FetchDestinationSwizzle swizzle = getDestSwizzle(dstSwizzle, i);
                if (swizzle == FetchDestinationSwizzle.Zero)
                {
                    indent();
                    println("r{}.{} = 0.0;", dstRegister, SWIZZLES[i]);
                }
                else if (swizzle == FetchDestinationSwizzle.One)
                {
                    indent();
                    println("r{}.{} = 1.0;", dstRegister, SWIZZLES[i]);
                }
            }
        }

        private void recompile(VertexFetchInstruction instr, uint address)
        {
            if (instr.isPredicated != 0)
            {
                indent();
                println("if ({}p0)", instr.predicateCondition != 0 ? "" : "!");

                indent();
                @out.Append("{\n");
                ++indentation;
            }

            indent();
            print("r{}.", instr.dstRegister);
            printDstSwizzle(instr.dstSwizzle, false);

            @out.Append(" = ");

            VertexElement findResult;
            if (!vertexElements.TryGetValue(address, out findResult))
                throw new InvalidOperationException("no vertex element at fetch address " + address.ToString(CultureInfo.InvariantCulture));

            switch (findResult.usage)
            {
                case DeclUsage.Normal:
                case DeclUsage.Tangent:
                case DeclUsage.Binormal:
                    specConstantsMask |= SPEC_CONSTANT_R11G11B10_NORMAL;
                    print("tfetchR11G11B10(");
                    break;

                case DeclUsage.TexCoord:
                    print("tfetchTexcoord(g_SwappedTexcoords, ");
                    break;
            }

            print("i{}{}", USAGE_VARIABLES[(uint)findResult.usage], (uint)findResult.usageIndex);

            switch (findResult.usage)
            {
                case DeclUsage.Normal:
                case DeclUsage.Tangent:
                case DeclUsage.Binormal:
                    @out.Append(')');
                    break;

                case DeclUsage.TexCoord:
                    print(", {})", (uint)findResult.usageIndex);
                    break;
            }

            @out.Append('.');
            printDstSwizzle(instr.dstSwizzle, true);

            @out.Append(";\n");

            printDstSwizzle01(instr.dstRegister, instr.dstSwizzle);

            if (instr.isPredicated != 0)
            {
                --indentation;
                indent();
                @out.Append("}\n");
            }
        }

        // the C++ lambda printSrcRegister
        private void InstrPrintSrcRegister(TextureFetchInstruction instr, uint componentCount)
        {
            print("r{}.", instr.srcRegister);

            for (uint i = 0; i < componentCount; i++)
                @out.Append(SWIZZLES[((instr.srcSwizzle >> (int)(i * 2))) & 0x3]);
        }

        // fmt's {} of offset * 0.5f: a 5-bit offset makes it a half-integer in [-8, 7.5], printed as 0, 7.5, -8
        private static string InstrFormatHalf(float v)
        {
            int twice = (int)(v * 2f);
            if (twice != v * 2f || twice < -16 || twice > 15)
                throw new FormatException("not a fetch offset: " + v.ToString("R", CultureInfo.InvariantCulture));
            int a = twice < 0 ? -twice : twice;
            return (twice < 0 ? "-" : "") + (a / 2).ToString(CultureInfo.InvariantCulture) + (a % 2 != 0 ? ".5" : "");
        }

        private void recompile(TextureFetchInstruction instr, bool bicubic)
        {
            if (instr.opcode != FetchOpcode.TextureFetch && instr.opcode != FetchOpcode.GetTextureWeights)
                return;

            if (instr.isPredicated != 0)
            {
                indent();
                println("if ({}p0)", instr.predCondition != 0 ? "" : "!");

                indent();
                @out.Append("{\n");
                ++indentation;
            }

            string constName = "";
            string constNamePtr = null;

            string findResult;
            if (samplers.TryGetValue(instr.constIndex, out findResult))
            {
                constNamePtr = findResult;
            }
            else
            {
                constName = format("s{}", instr.constIndex);
                constNamePtr = constName;
            }

            indent();
            print("r{}.", instr.dstRegister);
            printDstSwizzle(instr.dstSwizzle, false);

            @out.Append(" = ");
            switch (instr.opcode)
            {
                case FetchOpcode.TextureFetch:
                {
                    @out.Append("tfetch");
                    break;
                }
                case FetchOpcode.GetTextureWeights:
                {
                    @out.Append("getWeights");
                    break;
                }
            }

            string dimension = "";
            uint componentCount = 0;

            switch (instr.dimension)
            {
                case TextureDimension.Texture1D:
                    dimension = "1D";
                    componentCount = 1;
                    break;
                case TextureDimension.Texture2D:
                    dimension = "2D";
                    componentCount = 2;
                    break;
                case TextureDimension.Texture3D:
                    dimension = "3D";
                    componentCount = 3;
                    break;
                case TextureDimension.TextureCube:
                    dimension = "Cube";
                    componentCount = 3;
                    break;
            }

            @out.Append(dimension);

            print("({0}_Texture{1}DescriptorIndex, {0}_SamplerDescriptorIndex, ", constNamePtr, dimension);
            InstrPrintSrcRegister(instr, componentCount);

            switch (instr.dimension)
            {
                case TextureDimension.Texture2D:
                    print(", float2({}, {})", InstrFormatHalf(instr.offsetX * 0.5f), InstrFormatHalf(instr.offsetY * 0.5f));
                    break;
                case TextureDimension.TextureCube:
                    @out.Append(", cubeMapData");
                    break;
            }

            @out.Append(").");

            printDstSwizzle(instr.dstSwizzle, true);

            @out.Append(";\n");

            printDstSwizzle01(instr.dstRegister, instr.dstSwizzle);

            if (instr.isPredicated != 0)
            {
                --indentation;
                indent();
                @out.Append("}\n");
            }
        }

        private void recompile(AluInstruction instr)
        {
            if (instr.isPredicated != 0)
            {
                indent();
                println("if ({}p0)", instr.predicateCondition != 0 ? "" : "!");

                indent();
                @out.Append("{\n");
                ++indentation;
            }

            const uint VECTOR_0 = 0, VECTOR_1 = 1, VECTOR_2 = 2, SCALAR_0 = 3, SCALAR_1 = 4, SCALAR_CONSTANT_0 = 5, SCALAR_CONSTANT_1 = 6;

            // reg and swizzle are size_t in the C++
            Func<uint, string> op = delegate(uint operand)
            {
                ulong reg = 0;
                ulong swizzle = 0;
                bool select = true;
                bool negate = false;
                bool abs = false;

                switch (operand)
                {
                    case SCALAR_CONSTANT_0:
                        reg = instr.src3Register;
                        swizzle = instr.src3Swizzle;
                        select = false;
                        negate = instr.src3Negate != 0;
                        abs = instr.absConstants != 0;
                        break;

                    case SCALAR_CONSTANT_1:
                        reg = ((uint)instr.scalarOpcode & 1) | (instr.src3Select << 1) | (instr.src3Swizzle & 0x3C);
                        swizzle = instr.src3Swizzle;
                        select = true;
                        negate = instr.src3Negate != 0;
                        abs = instr.absConstants != 0;
                        break;

                    default:
                        switch (operand)
                        {
                            case VECTOR_0:
                                reg = instr.src1Register;
                                swizzle = instr.src1Swizzle;
                                select = instr.src1Select != 0;
                                negate = instr.src1Negate != 0;
                                break;
                            case VECTOR_1:
                                reg = instr.src2Register;
                                swizzle = instr.src2Swizzle;
                                select = instr.src2Select != 0;
                                negate = instr.src2Negate != 0;
                                break;
                            case VECTOR_2:
                            case SCALAR_0:
                            case SCALAR_1:
                                reg = instr.src3Register;
                                swizzle = instr.src3Swizzle;
                                select = instr.src3Select != 0;
                                negate = instr.src3Negate != 0;
                                break;
                        }

                        if (select)
                        {
                            abs = (reg & 0x80) != 0;
                            reg &= 0x3F;
                        }
                        else
                        {
                            abs = instr.absConstants != 0;
                        }

                        break;
                }

                string regFormatted;

                if (select)
                {
                    regFormatted = format("r{}", reg);
                }
                else
                {
                    ConstantInfo constant;
                    if (float4Constants.TryGetValue((uint)reg, out constant))
                    {
                        string constantName = constant.Name;
                        if (constant.registerCount > 1)
                        {
                            regFormatted = format("{}({}{})", constantName,
                                reg - constant.registerIndex, instr.const0Relative != 0 ? (instr.constAddressRegisterRelative != 0 ? " + a0" : " + aL") : "");
                        }
                        else
                        {
                            regFormatted = constantName;
                        }
                    }
                    else
                    {
                        regFormatted = format("c{}", reg);
                    }
                }

                string result = "";

                if (negate)
                    result += '-';

                if (abs)
                    result += "abs(";

                result += regFormatted;
                result += '.';

                switch (operand)
                {
                    case VECTOR_0:
                    case VECTOR_1:
                    case VECTOR_2:
                    {
                        uint mask;

                        switch (instr.vectorOpcode)
                        {
                            case AluVectorOpcode.Dp2Add:
                                mask = (operand == VECTOR_2) ? 0x1u : 0x3u;
                                break;

                            case AluVectorOpcode.Dp3:
                                mask = 0x7;
                                break;

                            case AluVectorOpcode.Dp4:
                            case AluVectorOpcode.Max4:
                                mask = 0xF;
                                break;

                            default:
                                mask = instr.vectorWriteMask != 0 ? instr.vectorWriteMask : 0x1u;
                                break;
                        }

                        for (ulong i = 0; i < 4; i++)
                        {
                            if (((mask >> (int)i) & 0x1) != 0)
                                result += SWIZZLES[((swizzle >> (int)(i * 2)) + i) & 0x3];
                        }

                        break;
                    }

                    case SCALAR_0:
                    case SCALAR_CONSTANT_0:
                        result += SWIZZLES[((swizzle >> 6) + 3) & 0x3];
                        break;

                    case SCALAR_1:
                    case SCALAR_CONSTANT_1:
                        result += SWIZZLES[swizzle & 0x3];
                        break;
                }

                if (abs)
                    result += ")";

                return result;
            };

            switch (instr.vectorOpcode)
            {
                case AluVectorOpcode.KillEq:
                    indent();
                    println("clip(any({} == {}) ? -1 : 1);", op(VECTOR_0), op(VECTOR_1));
                    break;

                case AluVectorOpcode.KillGt:
                    indent();
                    println("clip(any({} > {}) ? -1 : 1);", op(VECTOR_0), op(VECTOR_1));
                    break;

                case AluVectorOpcode.KillGe:
                    indent();
                    println("clip(any({} >= {}) ? -1 : 1);", op(VECTOR_0), op(VECTOR_1));
                    break;

                case AluVectorOpcode.KillNe:
                    indent();
                    println("clip(any({} != {}) ? -1 : 1);", op(VECTOR_0), op(VECTOR_1));
                    break;
            }

            bool closeIfBracket = false;

            string exportRegister = "";
            if (instr.exportData != 0)
            {
                if (isPixelShader)
                {
                    switch ((ExportRegister)instr.vectorDest)
                    {
                        case ExportRegister.PSColor0:
                            exportRegister = "oC0";
                            break;
                        case ExportRegister.PSColor1:
                            exportRegister = "oC1";
                            break;
                        case ExportRegister.PSColor2:
                            exportRegister = "oC2";
                            break;
                        case ExportRegister.PSColor3:
                            exportRegister = "oC3";
                            break;
                        case ExportRegister.PSDepth:
                            exportRegister = "oDepth";
                            break;
                    }
                }
                else
                {
                    switch ((ExportRegister)instr.vectorDest)
                    {
                        case ExportRegister.VSPosition:
                            exportRegister = "oPos";
                            break;

                        default:
                        {
                            string interpolator;
                            if (!interpolators.TryGetValue(instr.vectorDest, out interpolator))
                                throw new InvalidOperationException("no interpolator for export register " + instr.vectorDest.ToString(CultureInfo.InvariantCulture));
                            exportRegister = interpolator;
                            break;
                        }
                    }
                }
            }

            if (instr.vectorOpcode >= AluVectorOpcode.SetpEqPush && instr.vectorOpcode <= AluVectorOpcode.SetpGePush)
            {
                indent();
                print("p0 = {} == 0.0 && {} ", op(VECTOR_0), op(VECTOR_1));

                switch (instr.vectorOpcode)
                {
                    case AluVectorOpcode.SetpEqPush:
                        @out.Append("==");
                        break;
                    case AluVectorOpcode.SetpNePush:
                        @out.Append("!=");
                        break;
                    case AluVectorOpcode.SetpGtPush:
                        @out.Append(">");
                        break;
                    case AluVectorOpcode.SetpGePush:
                        @out.Append(">=");
                        break;
                }

                @out.Append(" 0.0;\n");
            }
            else if (instr.vectorOpcode >= AluVectorOpcode.MaxA)
            {
                indent();
                println("a0 = (int)clamp(floor(({}).w + 0.5), -256.0, 255.0);", op(VECTOR_0));
            }

            uint vectorWriteMask = instr.vectorWriteMask;
            if (instr.exportData != 0)
                vectorWriteMask &= ~instr.scalarWriteMask;

            if (vectorWriteMask != 0)
            {
                indent();
                if (exportRegister.Length != 0)
                {
                    @out.Append(exportRegister);
                    @out.Append('.');
                }
                else
                {
                    print("r{}.", instr.vectorDest);
                }

                for (int i = 0; i < 4; i++)
                {
                    if (((vectorWriteMask >> i) & 0x1) != 0)
                        @out.Append(SWIZZLES[i]);
                }

                @out.Append(" = ");

                if (instr.vectorSaturate != 0)
                    @out.Append("saturate(");

                switch (instr.vectorOpcode)
                {
                    case AluVectorOpcode.Add:
                        print("{} + {}", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.Mul:
                        print("{} * {}", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.Max:
                    case AluVectorOpcode.MaxA:
                        print("max({}, {})", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.Min:
                        print("min({}, {})", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.Seq:
                        print("{} == {}", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.Sgt:
                        print("{} > {}", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.Sge:
                        print("{} >= {}", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.Sne:
                        print("{} != {}", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.Frc:
                        print("frac({})", op(VECTOR_0));
                        break;

                    case AluVectorOpcode.Trunc:
                        print("trunc({})", op(VECTOR_0));
                        break;

                    case AluVectorOpcode.Floor:
                        print("floor({})", op(VECTOR_0));
                        break;

                    case AluVectorOpcode.Mad:
                        print("{} * {} + {}", op(VECTOR_0), op(VECTOR_1), op(VECTOR_2));
                        break;

                    case AluVectorOpcode.CndEq:
                        print("select({} == 0.0, {}, {})", op(VECTOR_0), op(VECTOR_1), op(VECTOR_2));
                        break;

                    case AluVectorOpcode.CndGe:
                        print("select({} >= 0.0, {}, {})", op(VECTOR_0), op(VECTOR_1), op(VECTOR_2));
                        break;

                    case AluVectorOpcode.CndGt:
                        print("select({} > 0.0, {}, {})", op(VECTOR_0), op(VECTOR_1), op(VECTOR_2));
                        break;

                    case AluVectorOpcode.Dp4:
                    case AluVectorOpcode.Dp3:
                        print("dot({}, {})", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.Dp2Add:
                        print("dot({}, {}) + {}", op(VECTOR_0), op(VECTOR_1), op(VECTOR_2));
                        break;

                    case AluVectorOpcode.Cube:
                        print("cube(r{}, cubeMapData)", instr.src1Register);
                        break;

                    case AluVectorOpcode.Max4:
                        print("max4({})", op(VECTOR_0));
                        break;

                    case AluVectorOpcode.SetpEqPush:
                    case AluVectorOpcode.SetpNePush:
                    case AluVectorOpcode.SetpGtPush:
                    case AluVectorOpcode.SetpGePush:
                        print("p0 ? 0.0 : {} + 1.0", op(VECTOR_0));
                        break;

                    case AluVectorOpcode.KillEq:
                        print("any({} == {})", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.KillGt:
                        print("any({} > {})", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.KillGe:
                        print("any({} >= {})", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.KillNe:
                        print("any({} != {})", op(VECTOR_0), op(VECTOR_1));
                        break;

                    case AluVectorOpcode.Dst:
                        print("dst({}, {})", op(VECTOR_0), op(VECTOR_1));
                        break;
                }

                if (instr.vectorSaturate != 0)
                    @out.Append(')');

                @out.Append(";\n");
            }

            if (instr.scalarOpcode != AluScalarOpcode.RetainPrev)
            {
                if (instr.scalarOpcode >= AluScalarOpcode.SetpEq && instr.scalarOpcode <= AluScalarOpcode.SetpRstr)
                {
                    indent();
                    @out.Append("p0 = ");

                    switch (instr.scalarOpcode)
                    {
                        case AluScalarOpcode.SetpEq:
                            print("{} == 0.0", op(SCALAR_0));
                            break;

                        case AluScalarOpcode.SetpNe:
                            print("{} != 0.0", op(SCALAR_0));
                            break;

                        case AluScalarOpcode.SetpGt:
                            print("{} > 0.0", op(SCALAR_0));
                            break;

                        case AluScalarOpcode.SetpGe:
                            print("{} >= 0.0", op(SCALAR_0));
                            break;

                        case AluScalarOpcode.SetpInv:
                            print("{} == 1.0", op(SCALAR_0));
                            break;

                        case AluScalarOpcode.SetpPop:
                            print("{} - 1.0 <= 0.0", op(SCALAR_0));
                            break;

                        case AluScalarOpcode.SetpClr:
                            @out.Append("false");
                            break;

                        case AluScalarOpcode.SetpRstr:
                            print("{} == 0.0", op(SCALAR_0));
                            break;
                    }

                    @out.Append(";\n");
                }

                indent();
                @out.Append("ps = ");
                if (instr.scalarSaturate != 0)
                    @out.Append("saturate(");

                switch (instr.scalarOpcode)
                {
                    case AluScalarOpcode.Adds:
                        print("{} + {}", op(SCALAR_0), op(SCALAR_1));
                        break;

                    case AluScalarOpcode.AddsPrev:
                        print("{} + ps", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Muls:
                        print("{} * {}", op(SCALAR_0), op(SCALAR_1));
                        break;

                    case AluScalarOpcode.MulsPrev:
                    case AluScalarOpcode.MulsPrev2:
                        print("{} * ps", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Maxs:
                    case AluScalarOpcode.MaxAs:
                    case AluScalarOpcode.MaxAsf:
                        print("max({}, {})", op(SCALAR_0), op(SCALAR_1));
                        break;

                    case AluScalarOpcode.Mins:
                        print("min({}, {})", op(SCALAR_0), op(SCALAR_1));
                        break;

                    case AluScalarOpcode.Seqs:
                        print("{} == 0.0", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Sgts:
                        print("{} > 0.0", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Sges:
                        print("{} >= 0.0", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Snes:
                        print("{} != 0.0", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Frcs:
                        print("frac({})", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Truncs:
                        print("trunc({})", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Floors:
                        print("floor({})", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Exp:
                        print("exp2({})", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Logc:
                    case AluScalarOpcode.Log:
                        print("clamp(log2({}), FLT_MIN, FLT_MAX)", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Rcpc:
                    case AluScalarOpcode.Rcpf:
                    case AluScalarOpcode.Rcp:
                        print("clamp(rcp({}), FLT_MIN, FLT_MAX)", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Rsqc:
                    case AluScalarOpcode.Rsqf:
                    case AluScalarOpcode.Rsq:
                        print("clamp(rsqrt({}), FLT_MIN, FLT_MAX)", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Subs:
                        print("{} - {}", op(SCALAR_0), op(SCALAR_1));
                        break;

                    case AluScalarOpcode.SubsPrev:
                        print("{} - ps", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.SetpEq:
                    case AluScalarOpcode.SetpNe:
                    case AluScalarOpcode.SetpGt:
                    case AluScalarOpcode.SetpGe:
                        @out.Append("p0 ? 0.0 : 1.0");
                        break;

                    case AluScalarOpcode.SetpInv:
                        print("{0} == 0.0 ? 1.0 : {0}", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.SetpPop:
                        print("p0 ? 0.0 : ({} - 1.0)", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.SetpClr:
                        @out.Append("FLT_MAX");
                        break;

                    case AluScalarOpcode.SetpRstr:
                        print("p0 ? 0.0 : {}", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.KillsEq:
                        print("{} == 0.0", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.KillsGt:
                        print("{} > 0.0", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.KillsGe:
                        print("{} >= 0.0", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.KillsNe:
                        print("{} != 0.0", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.KillsOne:
                        print("{} == 1.0", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Sqrt:
                        print("sqrt({})", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Mulsc0:
                    case AluScalarOpcode.Mulsc1:
                        print("{} * {}", op(SCALAR_CONSTANT_0), op(SCALAR_CONSTANT_1));
                        break;

                    case AluScalarOpcode.Addsc0:
                    case AluScalarOpcode.Addsc1:
                        print("{} + {}", op(SCALAR_CONSTANT_0), op(SCALAR_CONSTANT_1));
                        break;

                    case AluScalarOpcode.Subsc0:
                    case AluScalarOpcode.Subsc1:
                        print("{} - {}", op(SCALAR_CONSTANT_0), op(SCALAR_CONSTANT_1));
                        break;

                    case AluScalarOpcode.Sin:
                        print("sin({})", op(SCALAR_0));
                        break;

                    case AluScalarOpcode.Cos:
                        print("cos({})", op(SCALAR_0));
                        break;
                }

                if (instr.scalarSaturate != 0)
                    @out.Append(')');

                @out.Append(";\n");

                switch (instr.scalarOpcode)
                {
                    case AluScalarOpcode.MaxAs:
                        indent();
                        println("a0 = (int)clamp(floor({} + 0.5), -256.0, 255.0);", op(SCALAR_0));
                        break;
                    case AluScalarOpcode.MaxAsf:
                        indent();
                        println("a0 = (int)clamp(floor({}), -256.0, 255.0);", op(SCALAR_0));
                        break;
                }
            }

            uint scalarWriteMask = instr.scalarWriteMask;
            if (instr.exportData != 0)
                scalarWriteMask &= ~instr.vectorWriteMask;

            if (scalarWriteMask != 0)
            {
                indent();
                if (exportRegister.Length != 0)
                {
                    @out.Append(exportRegister);
                    @out.Append('.');
                }
                else
                {
                    print("r{}.", instr.scalarDest);
                }

                for (int i = 0; i < 4; i++)
                {
                    if (((scalarWriteMask >> i) & 0x1) != 0)
                        @out.Append(SWIZZLES[i]);
                }

                @out.Append(" = ps;\n");
            }

            if (instr.exportData != 0)
            {
                uint zeroMask = instr.scalarDestRelative != 0 ? (0xFu & ~(instr.vectorWriteMask | instr.scalarWriteMask)) : 0;
                uint oneMask = instr.vectorWriteMask & instr.scalarWriteMask;

                for (int i = 0; i < 4; i++)
                {
                    uint mask = 1u << i;
                    if ((zeroMask & mask) != 0)
                    {
                        indent();
                        println("{}.{} = 0.0;", exportRegister, SWIZZLES[i]);
                    }
                    else if ((oneMask & mask) != 0)
                    {
                        indent();
                        println("{}.{} = 1.0;", exportRegister, SWIZZLES[i]);
                    }
                }
            }

            if (instr.scalarOpcode >= AluScalarOpcode.KillsEq && instr.scalarOpcode <= AluScalarOpcode.KillsOne)
            {
                indent();
                @out.Append("clip(ps != 0.0 ? -1 : 1);\n");
            }

            if (closeIfBracket)
            {
                --indentation;
                indent();
                @out.Append("}\n");
            }

            if (instr.isPredicated != 0)
            {
                --indentation;
                indent();
                @out.Append("}\n");
            }
        }
    }
}
