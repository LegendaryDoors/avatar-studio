// XenosRecomp's HLSL recompiler (hedge-dev/XenosRecomp 990d03b, shader_recompiler.cpp), ported to C#.
// Copyright (c) 2025 hedge-dev and contributors, MIT (XenosRecomp.LICENSE).
// This file: ShaderRecompiler::recompile(shaderData, include), the whole-shader pass.

using System;
using System.Collections.Generic;

namespace AvatarStudio
{
    sealed partial class XenosRecompiler
    {
        /// <summary>Float4Definition (8 bytes, big-endian).</summary>
        private struct ShaderFloat4Definition
        {
            public ushort registerIndex, count; public uint physicalOffset;

            public static ShaderFloat4Definition Read(byte[] d, int o)
            {
                return new ShaderFloat4Definition { registerIndex = Be.U16(d, o), count = Be.U16(d, o + 2), physicalOffset = Be.U32(d, o + 4) };
            }
        }

        /// <summary>Int4Definition: registerIndex, count, then `count` u32 values at `values`.</summary>
        private struct ShaderInt4Definition
        {
            public ushort registerIndex, count; public int values;   // offset of values[0]

            public static ShaderInt4Definition Read(byte[] d, int o)
            {
                return new ShaderInt4Definition { registerIndex = Be.U16(d, o), count = Be.U16(d, o + 2), values = o + 4 };
            }
        }

        private void recompile(byte[] shaderData, string include)
        {
            this.shaderData = shaderData;
            ShaderContainer shaderContainer = ShaderContainer.Read(shaderData);

            @out.Append(include);
            @out.Append('\n');

            isPixelShader = (shaderContainer.flags & 0x1) == 0;

            // ConstantTableContainer: a u32 size, then the ConstantTable
            int constantTableContainer = (int)shaderContainer.constantTableOffset;
            constantTableData = constantTableContainer + 4;
            ConstantTable constantTable = ConstantTable.Read(shaderData, constantTableData);

            @out.Append("#ifdef __spirv__\n\n");

            for (uint i = 0; i < constantTable.constants; i++)
            {
                ConstantInfo constantInfo = ConstantInfo.Read(shaderData,
                    constantTableData + (int)constantTable.constantInfo + (int)i * ConstantInfo.Size, constantTableData);

                string constantName = constantInfo.Name;

                switch (constantInfo.registerSet)
                {
                    case RegisterSet.Float4:
                    {
                        string shaderName = isPixelShader ? "Pixel" : "Vertex";

                        if (constantInfo.registerCount > 1)
                        {
                            uint tailCount = (uint)((isPixelShader ? 224 : 256) - constantInfo.registerIndex);

                            println("#define {}(INDEX) select((INDEX) < {}, vk::RawBufferLoad<float4>(g_PushConstants.{}ShaderConstants + ({} + min(INDEX, {})) * 16, 0x10), 0.0)",
                                constantName, tailCount, shaderName, constantInfo.registerIndex, tailCount - 1u);
                        }
                        else
                        {
                            println("#define {} vk::RawBufferLoad<float4>(g_PushConstants.{}ShaderConstants + {}, 0x10)",
                                constantName, shaderName, constantInfo.registerIndex * 16);
                        }

                        for (ushort j = 0; j < constantInfo.registerCount; j++)
                            Emplace(float4Constants, (uint)(constantInfo.registerIndex + j), constantInfo);

                        break;
                    }

                    case RegisterSet.Sampler:
                    {
                        for (ulong j = 0; j < (ulong)TEXTURE_DIMENSIONS.Length; j++)
                        {
                            println("#define {}_Texture{}DescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + {})",
                                constantName, TEXTURE_DIMENSIONS[j], j * 64 + (ulong)(constantInfo.registerIndex * 4));
                        }

                        println("#define {}_SamplerDescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + {})",
                            constantName, (ulong)TEXTURE_DIMENSIONS.Length * 64 + (ulong)(constantInfo.registerIndex * 4));

                        Emplace(samplers, (uint)constantInfo.registerIndex, constantName);
                        break;
                    }
                }
            }

            @out.Append("\n#else\n\n");

            println("cbuffer {}ShaderConstants : register(b{}, space4)", isPixelShader ? "Pixel" : "Vertex", isPixelShader ? 1 : 0);
            @out.Append("{\n");

            for (uint i = 0; i < constantTable.constants; i++)
            {
                ConstantInfo constantInfo = ConstantInfo.Read(shaderData,
                    constantTableData + (int)constantTable.constantInfo + (int)i * ConstantInfo.Size, constantTableData);

                if (constantInfo.registerSet == RegisterSet.Float4)
                {
                    string constantName = constantInfo.Name;

                    print("\tfloat4 {}", constantName);

                    if (constantInfo.registerCount > 1)
                        print("[{}]", constantInfo.registerCount);

                    println(" : packoffset(c{});", constantInfo.registerIndex);

                    if (constantInfo.registerCount > 1)
                    {
                        uint tailCount = (uint)((isPixelShader ? 224 : 256) - constantInfo.registerIndex);
                        println("#define {0}(INDEX) select((INDEX) < {1}, {0}[min(INDEX, {2})], 0.0)", constantName, tailCount, tailCount - 1u);
                    }
                }
            }

            @out.Append("};\n\n");

            @out.Append("cbuffer SharedConstants : register(b2, space4)\n");
            @out.Append("{\n");

            for (uint i = 0; i < constantTable.constants; i++)
            {
                ConstantInfo constantInfo = ConstantInfo.Read(shaderData,
                    constantTableData + (int)constantTable.constantInfo + (int)i * ConstantInfo.Size, constantTableData);

                if (constantInfo.registerSet == RegisterSet.Sampler)
                {
                    string constantName = constantInfo.Name;

                    for (ulong j = 0; j < (ulong)TEXTURE_DIMENSIONS.Length; j++)
                    {
                        println("\tuint {}_Texture{}DescriptorIndex : packoffset(c{}.{});",
                            constantName, TEXTURE_DIMENSIONS[j], j * 4 + (ulong)(constantInfo.registerIndex / 4), SWIZZLES[constantInfo.registerIndex % 4]);
                    }

                    println("\tuint {}_SamplerDescriptorIndex : packoffset(c{}.{});",
                        constantName, 4 * (ulong)TEXTURE_DIMENSIONS.Length + (ulong)(constantInfo.registerIndex / 4), SWIZZLES[constantInfo.registerIndex % 4]);
                }
            }

            @out.Append("\tDEFINE_SHARED_CONSTANTS();\n");
            @out.Append("};\n\n");

            @out.Append("#endif\n");

            for (uint i = 0; i < constantTable.constants; i++)
            {
                ConstantInfo constantInfo = ConstantInfo.Read(shaderData,
                    constantTableData + (int)constantTable.constantInfo + (int)i * ConstantInfo.Size, constantTableData);

                if (constantInfo.registerSet == RegisterSet.Bool)
                {
                    string constantName = constantInfo.Name;
                    println("\t#define {} (1 << {})", constantName, constantInfo.registerIndex + (isPixelShader ? 16 : 0));
                    Emplace(boolConstants, (uint)constantInfo.registerIndex, constantName);
                }
            }

            @out.Append('\n');

            int shaderOffset = (int)shaderContainer.shaderOffset;
            Shader shader = Shader.Read(shaderData, shaderOffset);

            @out.Append("#ifndef __spirv__\n");

            if (isPixelShader)
                @out.Append("[shader(\"pixel\")]\n");
            else
                @out.Append("[shader(\"vertex\")]\n");

            @out.Append("#endif\n");

            @out.Append("void main(\n");

            if (isPixelShader)
            {
                @out.Append("\tin float4 iPos : SV_Position,\n");

                foreach (KeyValuePair<DeclUsage, uint> pair in INTERPOLATORS)
                {
                    DeclUsage usage = pair.Key; uint usageIndex = pair.Value;
                    println("\tin float4 i{0}{1} : {2}{1},", USAGE_VARIABLES[(uint)usage], usageIndex, USAGE_SEMANTICS[(uint)usage]);
                }

                @out.Append("#ifdef __spirv__\n");
                @out.Append("\tin bool iFace : SV_IsFrontFace\n");
                @out.Append("#else\n");
                @out.Append("\tin uint iFace : SV_IsFrontFace\n");
                @out.Append("#endif\n");

                PixelShader pixelShader = PixelShader.Read(shaderData, shaderOffset);
                if ((pixelShader.outputs & PixelShaderOutputs.PIXEL_SHADER_OUTPUT_COLOR0) != 0)
                    @out.Append(",\n\tout float4 oC0 : SV_Target0");
                if ((pixelShader.outputs & PixelShaderOutputs.PIXEL_SHADER_OUTPUT_COLOR1) != 0)
                    @out.Append(",\n\tout float4 oC1 : SV_Target1");
                if ((pixelShader.outputs & PixelShaderOutputs.PIXEL_SHADER_OUTPUT_COLOR2) != 0)
                    @out.Append(",\n\tout float4 oC2 : SV_Target2");
                if ((pixelShader.outputs & PixelShaderOutputs.PIXEL_SHADER_OUTPUT_COLOR3) != 0)
                    @out.Append(",\n\tout float4 oC3 : SV_Target3");
                if ((pixelShader.outputs & PixelShaderOutputs.PIXEL_SHADER_OUTPUT_DEPTH) != 0)
                    @out.Append(",\n\tout float oDepth : SV_Depth");
            }
            else
            {
                VertexShader vertexShader = VertexShader.Read(shaderData, shaderOffset);
                for (uint i = 0; i < vertexShader.vertexElementCount; i++)
                {
                    uint value = Be.U32(shaderData, vertexShader.vertexElementsAndInterpolators + (int)(vertexShader.field18 + i) * 4);
                    VertexElement vertexElement = new VertexElement(value);

                    string usageType = USAGE_TYPES[(uint)vertexElement.usage];

                    @out.Append('\t');

                    foreach (DeclUsageLocation usageLocation in USAGE_LOCATIONS)
                    {
                        if (usageLocation.usage == vertexElement.usage && usageLocation.usageIndex == vertexElement.usageIndex)
                        {
                            print("[[vk::location({})]] ", usageLocation.location);
                            break;
                        }
                    }

                    println("in {0} i{1}{2} : {3}{2},", usageType, USAGE_VARIABLES[(uint)vertexElement.usage],
                        (uint)vertexElement.usageIndex, USAGE_SEMANTICS[(uint)vertexElement.usage]);

                    Emplace(vertexElements, (uint)vertexElement.address, vertexElement);
                }

                @out.Append("\tout float4 oPos : SV_Position");

                foreach (KeyValuePair<DeclUsage, uint> pair in INTERPOLATORS)
                {
                    DeclUsage usage = pair.Key; uint usageIndex = pair.Value;
                    print(",\n\tout float4 o{0}{1} : {2}{1}", USAGE_VARIABLES[(uint)usage], usageIndex, USAGE_SEMANTICS[(uint)usage]);
                }
            }

            @out.Append(")\n");
            @out.Append("{\n");

            if (shaderContainer.definitionTableOffset != 0)
            {
                // DefinitionTable: five u32 fields, then the definitions
                int definitionTable = (int)shaderContainer.definitionTableOffset;
                int definitions = definitionTable + 20;
                while (Be.U32(shaderData, definitions) != 0)
                {
                    ShaderFloat4Definition definition = ShaderFloat4Definition.Read(shaderData, definitions);
                    int value = (int)shaderContainer.virtualSize + (int)definition.physicalOffset;
                    for (ushort i = 0; i < (definition.count + 3) / 4; i++)
                    {
                        println("\tfloat4 c{} = asfloat(uint4(0x{:X}, 0x{:X}, 0x{:X}, 0x{:X}));",
                            definition.registerIndex + i - (isPixelShader ? 256 : 0), Be.U32(shaderData, value), Be.U32(shaderData, value + 4),
                            Be.U32(shaderData, value + 8), Be.U32(shaderData, value + 12));

                        value += 4 * 4;
                    }
                    definitions += 2 * 4;
                }
                definitions += 4;
                while (Be.U32(shaderData, definitions) != 0)
                {
                    ShaderInt4Definition definition = ShaderInt4Definition.Read(shaderData, definitions);
                    for (ushort i = 0; i < definition.count; i++)
                    {
                        // the union's int8_t x, y, z, w: the u32's bytes, lowest first
                        uint value = Be.U32(shaderData, definition.values + i * 4);
                        sbyte x = (sbyte)(byte)value, y = (sbyte)(byte)(value >> 8), z = (sbyte)(byte)(value >> 16), w = (sbyte)(byte)(value >> 24);

                        println("\tint4 i{} = int4({}, {}, {}, {});",
                            (definition.registerIndex - 8992) / 4 + i, (int)x, (int)y, (int)z, (int)w);
                    }
                    definitions += 2 * 4;
                    definitions += definition.count * 4;
                }

                @out.Append("\n");
            }

            bool[] printedRegisters = new bool[32];

            uint interpolatorCount = (shader.interpolatorInfo >> 5) & 0x1F;

            for (uint i = 0; i < interpolatorCount; i++)
            {
                if (isPixelShader)
                {
                    uint value = Be.U32(shaderData, PixelShader.Read(shaderData, shaderOffset).interpolators + (int)i * 4);
                    Interpolator interpolator = new Interpolator(value);
                    println("\tfloat4 r{} = i{}{};", (uint)interpolator.reg, USAGE_VARIABLES[(uint)interpolator.usage], (uint)interpolator.usageIndex);
                    printedRegisters[interpolator.reg] = true;
                }
                else
                {
                    VertexShader vertexShader = VertexShader.Read(shaderData, shaderOffset);
                    uint value = Be.U32(shaderData,
                        vertexShader.vertexElementsAndInterpolators + (int)(vertexShader.field18 + vertexShader.vertexElementCount + i) * 4);
                    Interpolator interpolator = new Interpolator(value);
                    Emplace(interpolators, i, format("o{}{}", USAGE_VARIABLES[(uint)interpolator.usage], (uint)interpolator.usageIndex));
                }
            }

            if (!isPixelShader)
            {
                foreach (KeyValuePair<DeclUsage, uint> pair in INTERPOLATORS)
                {
                    DeclUsage usage = pair.Key; uint usageIndex = pair.Value;
                    println("\to{}{} = 0.0;", USAGE_VARIABLES[(uint)usage], usageIndex);
                }

                @out.Append("\n");
            }

            for (ulong i = 0; i < 32; i++)
            {
                if (!printedRegisters[i])
                {
                    print("\tfloat4 r{} = ", i);
                    if (isPixelShader && i == ((shader.fieldC >> 8) & 0xFF))
                    {
                        @out.Append("float4((iPos.xy - 0.5) * float2(iFace ? 1.0 : -1.0, 1.0), 0.0, 0.0);\n");
                    }
                    else
                    {
                        @out.Append("0.0;\n");
                    }
                }
            }

            @out.Append("\tint a0 = 0;\n");
            @out.Append("\tint aL = 0;\n");
            @out.Append("\tbool p0 = false;\n");
            @out.Append("\tfloat ps = 0.0;\n");
            if (isPixelShader)
            {
                @out.Append("\tCubeMapData cubeMapData = (CubeMapData)0;\n");
            }

            // offset of the be<uint32_t> code words
            int code = (int)shaderContainer.virtualSize + (int)shader.physicalOffset;

            uint code0, code1, code2, code3;

            int controlFlowCode = code;
            uint instrAddress = 0;
            uint instrSize = shader.size;
            bool simpleControlFlow = true;

            while (instrAddress < instrSize)
            {
                code0 = Be.U32(shaderData, controlFlowCode);
                code1 = Be.U32(shaderData, controlFlowCode + 4) & 0xFFFF;
                code2 = (Be.U32(shaderData, controlFlowCode + 4) >> 16) | (Be.U32(shaderData, controlFlowCode + 8) << 16);
                code3 = Be.U32(shaderData, controlFlowCode + 8) >> 16;

                ControlFlowInstruction[] controlFlow = { new ControlFlowInstruction(code0, code1), new ControlFlowInstruction(code2, code3) };

                foreach (ControlFlowInstruction cfInstr in controlFlow)
                {
                    uint address = 0;

                    switch (cfInstr.opcode)
                    {
                        case ControlFlowOpcode.Exec:
                        case ControlFlowOpcode.ExecEnd:
                            address = cfInstr.exec.address;
                            break;

                        case ControlFlowOpcode.CondExec:
                        case ControlFlowOpcode.CondExecEnd:
                        case ControlFlowOpcode.CondExecPredClean:
                        case ControlFlowOpcode.CondExecPredCleanEnd:
                            address = cfInstr.condExec.address;
                            break;

                        case ControlFlowOpcode.CondExecPred:
                        case ControlFlowOpcode.CondExecPredEnd:
                            address = cfInstr.condExecPred.address;
                            break;

                        case ControlFlowOpcode.CondJmp:
                        {
                            if (cfInstr.condJmp.isUnconditional != 0 || cfInstr.condJmp.direction != 0)
                                simpleControlFlow = false;
                            else
                            {
                                uint labels;
                                ifEndLabels.TryGetValue(cfInstr.condJmp.address, out labels);
                                ifEndLabels[cfInstr.condJmp.address] = labels + 1;
                            }

                            break;
                        }
                    }

                    if (address != 0)
                        instrSize = Math.Min(instrSize, address * 12);
                }

                controlFlowCode += 3 * 4;
                instrAddress += 12;
            }

            if (simpleControlFlow)
            {
                @out.Append('\n');
                indentation = 1;
            }
            else
            {
                @out.Append("\n\tuint pc = 0;\n");
                @out.Append("\twhile (true)\n");
                @out.Append("\t{\n");
                @out.Append("\t\tswitch (pc)\n");
                @out.Append("\t\t{\n");
            }

            controlFlowCode = code;
            instrAddress = 0;
            uint pc = 0;

            while (instrAddress < instrSize)
            {
                code0 = Be.U32(shaderData, controlFlowCode);
                code1 = Be.U32(shaderData, controlFlowCode + 4) & 0xFFFF;
                code2 = (Be.U32(shaderData, controlFlowCode + 4) >> 16) | (Be.U32(shaderData, controlFlowCode + 8) << 16);
                code3 = Be.U32(shaderData, controlFlowCode + 8) >> 16;

                ControlFlowInstruction[] controlFlow = { new ControlFlowInstruction(code0, code1), new ControlFlowInstruction(code2, code3) };

                foreach (ControlFlowInstruction cfInstr in controlFlow)
                {
                    if (!simpleControlFlow)
                    {
                        indentation = 3;
                        println("\t\tcase {}:", pc);
                    }
                    else
                    {
                        uint findResult;
                        if (ifEndLabels.TryGetValue(pc, out findResult))
                        {
                            for (uint i = 0; i < findResult; i++)
                            {
                                --indentation;
                                indent();
                                @out.Append("}\n");
                            }
                        }
                    }

                    ++pc;

                    uint address = 0;
                    uint count = 0;
                    uint sequence = 0;
                    bool shouldReturn = false;
                    bool shouldCloseCurlyBracket = false;

                    switch (cfInstr.opcode)
                    {
                        case ControlFlowOpcode.Exec:
                        case ControlFlowOpcode.ExecEnd:
                            address = cfInstr.exec.address;
                            count = cfInstr.exec.count;
                            sequence = cfInstr.exec.sequence;
                            shouldReturn = (cfInstr.opcode == ControlFlowOpcode.ExecEnd);
                            break;

                        case ControlFlowOpcode.CondExec:
                        case ControlFlowOpcode.CondExecEnd:
                        case ControlFlowOpcode.CondExecPredClean:
                        case ControlFlowOpcode.CondExecPredCleanEnd:
                            address = cfInstr.condExec.address;
                            count = cfInstr.condExec.count;
                            sequence = cfInstr.condExec.sequence;
                            // upstream tests CondExecEnd twice
                            shouldReturn = (cfInstr.opcode == ControlFlowOpcode.CondExecEnd || cfInstr.opcode == ControlFlowOpcode.CondExecEnd);
                            break;

                        case ControlFlowOpcode.CondExecPred:
                        case ControlFlowOpcode.CondExecPredEnd:
                            address = cfInstr.condExecPred.address;
                            count = cfInstr.condExecPred.count;
                            sequence = cfInstr.condExecPred.sequence;
                            shouldReturn = (cfInstr.opcode == ControlFlowOpcode.CondExecPredEnd);
                            break;

                        case ControlFlowOpcode.LoopStart:
                            if (simpleControlFlow)
                            {
                                indent();
                                println("for (aL = 0; aL < i{}.x; aL++)", (uint)cfInstr.loopStart.loopId);
                                indent();
                                @out.Append("{\n");
                                ++indentation;
                            }
                            else
                            {
                                @out.Append("\t\t\taL = 0;\n");
                            }
                            break;

                        case ControlFlowOpcode.LoopEnd:
                            if (simpleControlFlow)
                            {
                                --indentation;
                                indent();
                                @out.Append("}\n");
                            }
                            else
                            {
                                @out.Append("\t\t\t++aL;\n");
                                println("\t\t\tif (aL < i{}.x)", (uint)cfInstr.loopEnd.loopId);
                                @out.Append("\t\t\t{\n");
                                println("\t\t\t\tpc = {};", (uint)cfInstr.loopEnd.address);
                                @out.Append("\t\t\t\tcontinue;\n");
                                @out.Append("\t\t\t}\n");
                            }
                            break;

                        case ControlFlowOpcode.CondJmp:
                        {
                            if (cfInstr.condJmp.isUnconditional != 0)
                            {
                                println("\t\t\tpc = {};", (uint)cfInstr.condJmp.address);
                                @out.Append("\t\t\tcontinue;\n");
                            }
                            else
                            {
                                // C++: condition ^ simpleControlFlow ? a : b, the xor taken first
                                bool conditionXor = (cfInstr.condJmp.condition ^ (simpleControlFlow ? 1u : 0u)) != 0;

                                indent();
                                if (cfInstr.condJmp.isPredicated != 0)
                                {
                                    println("if ({}p0)", conditionXor ? "" : "!");
                                }
                                else
                                {
                                    string findResult;
                                    if (boolConstants.TryGetValue(cfInstr.condJmp.boolAddress, out findResult))
                                        println("if ((g_Booleans & {}) {}= 0)", findResult, conditionXor ? "!" : "=");
                                    else
                                        println("if (b{} {}= 0)", (uint)cfInstr.condJmp.boolAddress, conditionXor ? "!" : "=");
                                }

                                if (simpleControlFlow)
                                {
                                    indent();
                                    @out.Append("{\n");
                                    ++indentation;
                                }
                                else
                                {
                                    @out.Append("\t\t\t{\n");
                                    println("\t\t\t\tpc = {};", (uint)cfInstr.condJmp.address);
                                    @out.Append("\t\t\t\tcontinue;\n");
                                    @out.Append("\t\t\t}\n");
                                }
                            }
                            break;
                        }
                    }

                    int instructionCode = code + (int)(address * 3) * 4;

                    for (uint i = 0; i < count; i++)
                    {
                        // the union's code0..code2 (C# cannot reuse the outer names)
                        uint instrCode0 = Be.U32(shaderData, instructionCode);
                        uint instrCode1 = Be.U32(shaderData, instructionCode + 4);
                        uint instrCode2 = Be.U32(shaderData, instructionCode + 8);
                        VertexFetchInstruction vertexFetch = new VertexFetchInstruction(instrCode0, instrCode1, instrCode2);
                        TextureFetchInstruction textureFetch = new TextureFetchInstruction(instrCode0, instrCode1, instrCode2);
                        AluInstruction alu = new AluInstruction(instrCode0, instrCode1, instrCode2);

                        if ((sequence & 0x1) != 0)
                        {
                            if (vertexFetch.opcode == FetchOpcode.VertexFetch)
                            {
                                recompile(vertexFetch, address + i);
                            }
                            else
                            {
                                recompile(textureFetch, false);
                            }
                        }
                        else
                        {
                            recompile(alu);
                        }

                        sequence >>= 2;
                        instructionCode += 3 * 4;
                    }

                    if (shouldReturn)
                    {
                        if (isPixelShader)
                        {
                            specConstantsMask |= SPEC_CONSTANT_ALPHA_TEST;

                            indent();
                            @out.Append("[branch] if (g_SpecConstants() & SPEC_CONSTANT_ALPHA_TEST)");
                            indent();
                            @out.Append('{');

                            indent();
                            @out.Append("\tclip(oC0.w - g_AlphaThreshold);\n");

                            indent();
                            @out.Append("}");
                        }
                        else
                        {
                            @out.Append("\toPos.xy += g_HalfPixelOffset * oPos.w;\n");
                        }

                        if (simpleControlFlow)
                        {
                            indent();
                            @out.Append("return;\n");
                        }
                        else
                        {
                            @out.Append("\t\t\tbreak;\n");
                        }
                    }

                    if (shouldCloseCurlyBracket)
                    {
                        --indentation;
                        indent();
                        @out.Append("}\n");
                    }
                }

                controlFlowCode += 3 * 4;
                instrAddress += 12;
            }

            if (!simpleControlFlow)
            {
                @out.Append("\t\t\tbreak;\n");
                @out.Append("\t\t}\n");
                @out.Append("\t\tbreak;\n");
                @out.Append("\t}\n");
            }

            @out.Append("}");
        }
    }
}
