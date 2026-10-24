/**
 * 日期时间展示工具。
 */

/**
 * 把数字补零到两位。
 * @param value 待补零的数字
 * @returns 两位字符串
 */
function pad(value: number): string {
  return String(value).padStart(2, '0');
}

/**
 * 把接口时间格式化为 yyyy-MM-dd HH:mm。
 * @param value ISO 时间字符串
 * @returns 格式化后的时间；非法值返回空串
 */
export function formatDateTime(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return '';
  }

  const datePart = `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
  const timePart = `${pad(date.getHours())}:${pad(date.getMinutes())}`;
  return `${datePart} ${timePart}`;
}
