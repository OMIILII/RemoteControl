// NativeLibChecker.cs - 启动原生库自检
//
// 目的：程序一启动就验证核心原生库 rc_core.dll（抓屏/编码/解码/输入核心，
// 通过 P/Invoke 调用）及其依赖是否能正常加载。一旦缺失，直接弹窗列出
// 具体缺了哪个库，而不是在运行到某个 P/Invoke 调用时才抛出
// DllNotFoundException（那样的错误信息对用户极不友好）。
//
// 设计要点：
//   - 优先用 NativeLibrary.TryLoad 真实加载 rc_core.dll；只要加载成功就放行，
//     不弹窗（PE 解析只在加载失败时才用于定位缺失的依赖，因此解析器即使
//     有瑕疵也不会造成误报）。
//   - 解析 PE 导入表只依赖 .NET 自带 API，不引入额外依赖。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteControl
{
    internal static class NativeLibChecker
    {
        // Windows 自带、一定可用的系统 DLL，无需放入发布目录，直接忽略。
        private static readonly HashSet<string> SystemDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KERNEL32.DLL", "USER32.DLL", "GDI32.DLL", "ADVAPI32.DLL", "WS2_32.DLL", "OLE32.DLL",
            "SHELL32.DLL", "MSVCRT.DLL", "NTDLL.DLL", "COMCTL32.DLL", "COMDLG32.DLL", "WINMM.DLL",
            "IMM32.DLL", "SETUPAPI.DLL", "VERSION.DLL", "SHLWAPI.DLL", "RPCRT4.DLL", "SECHOST.DLL",
            "KERNELBASE.DLL", "CRYPT32.DLL", "BCRYPT.DLL", "USERENV.DLL", "PROFAPI.DLL", "OLEAUT32.DLL",
            "GDI32FULL.DLL", "MSCTF.DLL", "DWMAPI.DLL", "UXTHEME.DLL", "NETAPI32.DLL", "WLDAP32.DLL",
        };

        /// <summary>
        /// 验证核心原生库可用。返回 true 表示一切正常；false 时 problems 列出缺失/异常的库。
        /// </summary>
        public static bool Verify(out List<string> problems)
        {
            problems = new List<string>();
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string rcPath = Path.Combine(baseDir, "rc_core.dll");
                if (!File.Exists(rcPath))
                {
                    problems.Add("rc_core.dll 未找到（核心编解码/控制库缺失）");
                    return false;
                }

                // 真实加载整条原生依赖链。
                bool loaded = NativeLibrary.TryLoad("rc_core", out IntPtr h) ||
                              NativeLibrary.TryLoad(rcPath, out h);
                if (!loaded)
                {
                    problems.Add("rc_core.dll 加载失败（很可能是它的某个依赖库缺失或文件损坏）");
                    // 加载失败时才解析导入表，定位具体缺了哪个依赖库。
                    foreach (var imp in ReadImportDlls(rcPath))
                    {
                        if (IsSystemOrApiSet(imp)) continue;
                        string p = Path.Combine(baseDir, imp);
                        if (!File.Exists(p))
                            problems.Add("缺失依赖库: " + imp);
                    }
                }
            }
            catch (Exception ex)
            {
                if (problems.Count == 0)
                    problems.Add("原生库自检异常: " + ex.Message);
            }
            return problems.Count == 0;
        }

        private static bool IsSystemOrApiSet(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (SystemDlls.Contains(name)) return true;
            // Windows 现代版本在操作系统中直接提供，缺失通常不代表安装损坏。
            if (name.StartsWith("API-MS-WIN", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("EXT-MS-WIN", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---- 极简 PE 导入表解析（无外部依赖） ----
        private static List<string> ReadImportDlls(string path)
        {
            var result = new List<string>();
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch { return result; }
            try
            {
                if (data.Length < 64) return result;
                if (data[0] != 'M' || data[1] != 'Z') return result;
                int e_lfanew = BitConverter.ToInt32(data, 0x3C);
                if (e_lfanew < 0 || e_lfanew + 4 >= data.Length) return result;
                if (data[e_lfanew] != 'P' || data[e_lfanew + 1] != 'E') return result;

                int coffOffset = e_lfanew + 4;
                int sizeOfOptionalHeader = BitConverter.ToUInt16(data, coffOffset + 16);
                int numberOfSections = BitConverter.ToUInt16(data, coffOffset + 2);
                int optHeaderOffset = coffOffset + 20; // COFF 头固定 20 字节，其后即可选头
                if (optHeaderOffset + 2 > data.Length) return result;
                ushort magic = BitConverter.ToUInt16(data, optHeaderOffset);

                int dataDirOffset;
                if (magic == 0x20B)        // PE32+
                    dataDirOffset = optHeaderOffset + 112;
                else if (magic == 0x10B)   // PE32
                    dataDirOffset = optHeaderOffset + 96;
                else
                    return result;

                // 数据目录：索引 0 = 导出表，索引 1 = 导入表（每项 8 字节：RVA+大小）。
                int importRva = BitConverter.ToInt32(data, dataDirOffset + 8);
                if (importRva == 0) return result;

                int sectionTableOffset = optHeaderOffset + sizeOfOptionalHeader;

                int RvaToOffset(int rva)
                {
                    for (int i = 0; i < numberOfSections; i++)
                    {
                        int sh = sectionTableOffset + i * 40;
                        if (sh + 20 > data.Length) break;
                        int va = BitConverter.ToInt32(data, sh + 12);
                        int sz = BitConverter.ToInt32(data, sh + 16);
                        int raw = BitConverter.ToInt32(data, sh + 20);
                        if (rva >= va && rva < va + sz) return raw + (rva - va);
                    }
                    return -1;
                }

                int descOffset = RvaToOffset(importRva);
                if (descOffset < 0) return result;

                // IMAGE_IMPORT_DESCRIPTOR 每项 20 字节，末尾为全零项。
                for (int off = descOffset; off + 20 <= data.Length; off += 20)
                {
                    int nameRva = BitConverter.ToInt32(data, off + 12);
                    if (nameRva == 0) break;
                    int nameOff = RvaToOffset(nameRva);
                    if (nameOff < 0 || nameOff >= data.Length) continue;
                    int end = nameOff;
                    while (end < data.Length && data[end] != 0) end++;
                    if (end > nameOff)
                    {
                        string name = Encoding.ASCII.GetString(data, nameOff, end - nameOff);
                        if (!string.IsNullOrEmpty(name)) result.Add(name);
                    }
                }
            }
            catch { /* best effort */ }
            return result;
        }
    }
}
