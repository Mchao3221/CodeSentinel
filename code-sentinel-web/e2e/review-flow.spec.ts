import { expect, test } from '@playwright/test';

import { createReviewApiState, installReviewApi, API_ROUTE } from './fixtures/reviewApi';

/**
 * CodeSentinel 的 UI 冒烟测试：接口全部在浏览器侧打桩，不依赖后端、数据库与 git。
 */
test.describe('CodeSentinel 评审闭环', () => {
  test('提交 → 评论 → 要求修改 → 新版本 → 通过 → 已合并', async ({ browser }) => {
    const state = createReviewApiState();

    // 提交人与审核人各自独立会话，每个会话安装固定当前用户的打桩
    const submitterContext = await browser.newContext();
    const reviewerContext = await browser.newContext();
    await installReviewApi(submitterContext, state, 'submitter');
    await installReviewApi(reviewerContext, state, 'reviewer');

    const submitter = await submitterContext.newPage();
    const reviewer = await reviewerContext.newPage();

    // 提交人进入评审列表：路由守卫通过 GET /api/auth/me 完成会话探测
    await submitter.goto('/reviews');
    await expect(submitter.locator('.review-list-title')).toHaveText('评审');
    await expect(submitter.locator('.el-table__empty-text')).toHaveText('暂无评审单');

    // 新建评审：选仓库后目标分支自动预选 main，再选分支、审核人与标题
    await submitter.getByRole('button', { name: '新建评审' }).click();
    await expect(submitter).toHaveURL(/\/reviews\/new$/);

    // Element Plus 的下拉占位符渲染为文本而非 input 属性，按表单项顺序定位下拉
    const selects = submitter.locator('.review-create-form .el-select');

    await selects.nth(0).click();
    await submitter
      .locator('.el-select-dropdown:visible .el-select-dropdown__item')
      .filter({ hasText: '示例仓库' })
      .click();

    await selects.nth(2).click();
    await submitter
      .locator('.el-select-dropdown:visible .el-select-dropdown__item')
      .filter({ hasText: 'feature/login-rate-limit' })
      .click();

    await selects.nth(3).click();
    await submitter
      .locator('.el-select-dropdown:visible .el-select-dropdown__item')
      .filter({ hasText: '审核员' })
      .click();
    await submitter.keyboard.press('Escape');

    await submitter.getByPlaceholder('请输入标题').fill('实现登录限流');
    await submitter
      .getByPlaceholder('请输入评审说明（可选）')
      .fill('为登录接口增加尝试次数限制');
    await submitter.getByRole('button', { name: '提交评审' }).click();

    // 创建成功后进入详情，状态为评审中
    await expect(submitter).toHaveURL(/\/reviews\/review-1$/);
    await expect(submitter.locator('.review-header-title')).toHaveText('实现登录限流');
    await expect(submitter.locator('.review-header-title-row .el-tag')).toHaveText('评审中');

    // 夹具已校验并记录创建载荷，这里复核关键字段
    expect(state.lastCreateReview).toMatchObject({
      repoId: 'repo-1',
      baseBranch: 'main',
      headBranch: 'feature/login-rate-limit',
      title: '实现登录限流',
      reviewerIds: ['u-reviewer'],
    });

    // 展开 diff：一个上下文行 + 一个新增行，新增行上写行内评论
    await submitter.locator('.diff-file-toggle').click();
    await expect(submitter.locator('.diff-hunk-header')).toContainText('@@ -1,1 +1,2 @@');
    await expect(submitter.locator('.diff-line-add')).toContainText('MAX_ATTEMPTS = 5');
    await submitter.locator('.diff-line-add').hover();
    await submitter.locator('.diff-line-add .diff-comment-add').click();
    await submitter.locator('.comment-composer-input textarea').fill('请补充最大尝试次数的配置来源');
    await submitter.locator('.comment-composer-submit').click();
    await expect(submitter.locator('.comment-body')).toContainText('请补充最大尝试次数的配置来源');

    // 夹具已校验并记录评论载荷，这里复核锚点与内容
    expect(state.lastCreateComment).toMatchObject({
      path: 'src/auth/login.ts',
      side: 'NEW',
      line: 2,
      body: '请补充最大尝试次数的配置来源',
    });
    expect(state.lastCreateComment?.parentId ?? null).toBeNull();

    // 审核人从“待我评审”列表进入详情，能看到同一行评论
    await reviewer.goto('/reviews');
    await expect(reviewer.locator('.review-list-title')).toHaveText('评审');
    await reviewer.locator('.el-table__row').filter({ hasText: '实现登录限流' }).click();
    await expect(reviewer).toHaveURL(/\/reviews\/review-1$/);
    await reviewer.locator('.diff-file-toggle').click();
    await expect(reviewer.locator('.comment-body')).toContainText('请补充最大尝试次数的配置来源');

    // 要求修改：附言必填
    await reviewer.getByRole('button', { name: '要求修改' }).click();
    await expect(reviewer.locator('.request-changes-dialog')).toBeVisible();
    await reviewer
      .locator('.request-changes-dialog textarea')
      .fill('请补充配置来源并补一条测试');
    await reviewer.locator('.confirm-request-changes-button').click();
    await expect(reviewer.locator('.review-header-title-row .el-tag')).toHaveText('需修改');
    await expect(
      reviewer.locator('.reviewer-state-tag').filter({ hasText: '要求修改' }),
    ).toBeVisible();

    // 提交人刷新：新 head 生成 v2，旧结论作废，状态回到评审中
    await submitter.locator('.refresh-button').click();
    await expect(submitter.locator('.review-header-revision')).toContainText('v2');
    await expect(submitter.locator('.review-header-title-row .el-tag')).toHaveText('评审中');

    // 审核人重新加载详情后通过，出现合并指引
    await reviewer.reload();
    await expect(reviewer.locator('.review-header-revision')).toContainText('v2');
    await reviewer.getByRole('button', { name: '通过' }).click();
    await expect(reviewer.locator('.review-header-title-row .el-tag')).toHaveText('已通过');
    await expect(reviewer.locator('.merge-guide')).toBeVisible();

    // 合并人在 Gitblit 合并后手工标记已合并
    await reviewer.getByRole('button', { name: '标记为已合并' }).click();
    await expect(reviewer.locator('.mark-merged-dialog')).toBeVisible();
    await reviewer.locator('.confirm-mark-merged-button').click();
    await expect(reviewer.locator('.review-header-title-row .el-tag')).toHaveText('已合并');
    await expect(reviewer.locator('.review-merged-info')).toContainText('已合并');

    await submitterContext.close();
    await reviewerContext.close();
  });

  test('未登录访问评审列表会跳转登录页并保留回跳地址', async ({ page }) => {
    // 会话探测固定返回 401，模拟未登录
    await page.route(API_ROUTE, (route) =>
      route.fulfill({
        status: 401,
        contentType: 'application/json; charset=utf-8',
        body: JSON.stringify({ code: 'UNAUTHENTICATED', message: '未登录', traceId: 'e2e' }),
      }),
    );

    await page.goto('/reviews');

    await expect(page).toHaveURL(/\/login\?redirect=/);
    expect(new URL(page.url()).searchParams.get('redirect')).toBe('/reviews');
    await expect(page.locator('.login-submit')).toBeVisible();
  });
});
