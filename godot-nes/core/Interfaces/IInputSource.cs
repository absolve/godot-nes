namespace GodotNes.Core;

/// <summary>
/// 手柄状态来源。<paramref name="port"/> = 0 对应 $4016，= 1 对应 $4017。
/// </summary>
public interface IInputSource
{
	byte ReadButtons(int port);
}
