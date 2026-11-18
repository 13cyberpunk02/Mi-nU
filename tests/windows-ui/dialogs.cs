public delegate bool Cb(System.IntPtr h, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(Cb cb, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(System.IntPtr h, out int pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(System.IntPtr h, System.Text.StringBuilder s, int n);
/// <summary>The visible common dialog (#32770) of a process, or zero.</summary>
public static System.IntPtr Dialog(int pid) {
  var found = System.IntPtr.Zero;
  EnumWindows((h, l) => { int id; GetWindowThreadProcessId(h, out id); if (id == pid && IsWindowVisible(h)) { var c = new System.Text.StringBuilder(64); GetClassName(h, c, 64); if (c.ToString() == "#32770") { found = h; return false; } } return true; }, System.IntPtr.Zero);
  return found;
}
