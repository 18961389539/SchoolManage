namespace School.Training.Tests;

public class ClassroomCollaborationLogicTests
{
    [Fact]
    public void Validates_group_name_and_remark()
    {
        Assert.Null(ClassroomCollaborationLogic.ValidateGroup(new ClassroomGroupInput("第一组", Remark: "备注")));
        Assert.Equal("小组名称需为 1–100 个字符", ClassroomCollaborationLogic.ValidateGroup(new ClassroomGroupInput(" ")));
        Assert.Equal("小组名称需为 1–100 个字符", ClassroomCollaborationLogic.ValidateGroup(new ClassroomGroupInput(new string('组', 101))));
        Assert.Equal("小组名称不能包含控制字符", ClassroomCollaborationLogic.ValidateGroup(new ClassroomGroupInput("第一\u0001组")));
        Assert.Equal("小组备注最多 500 个字符", ClassroomCollaborationLogic.ValidateGroup(new ClassroomGroupInput("第一组", Remark: new string('备', 501))));
    }

    [Theory]
    [InlineData(0, "group", "resource", "每次选择 1–100 个机柜")]
    [InlineData(101, "group", "resource", "每次选择 1–100 个机柜")]
    [InlineData(1, "", "resource", "请选择小组")]
    [InlineData(1, "group", " ", "请选择案例实操题目")]
    [InlineData(1, "group", "resource", null)]
    public void Validates_assignment_arguments(int cabinets, string groupId, string resourceId, string? expected)
    {
        Assert.Equal(expected, ClassroomCollaborationLogic.ValidateAssignment(cabinets, groupId, resourceId));
    }

    [Fact]
    public void Detects_supported_image_signatures_only()
    {
        Assert.Equal(".png", ClassroomCollaborationLogic.DetectImageExtension(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        Assert.Equal(".jpg", ClassroomCollaborationLogic.DetectImageExtension(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Equal(".webp", ClassroomCollaborationLogic.DetectImageExtension("RIFF1234WEBP"u8));
        Assert.Null(ClassroomCollaborationLogic.DetectImageExtension("GIF89a"u8));
        Assert.Null(ClassroomCollaborationLogic.DetectImageExtension(ReadOnlySpan<byte>.Empty));
        Assert.Null(ClassroomCollaborationLogic.DetectImageExtension("RIFF1234WAVE"u8));
    }
}

public class ProfileLogicTests
{
    [Fact]
    public void Validates_profile_fields()
    {
        Assert.Null(ProfileLogic.Validate(new ProfileInput("张三", "2026001", "男", "13800000000", "zhangsan@example.com"), requireNumber: true));
        Assert.Null(ProfileLogic.Validate(new ProfileInput("管理员"), requireNumber: false));
        Assert.Equal("学号/工号不能为空", ProfileLogic.Validate(new ProfileInput("张三"), requireNumber: true));
        Assert.Equal("姓名必填，且最多 100 个字符", ProfileLogic.Validate(new ProfileInput(" "), requireNumber: false));
        Assert.Equal("学号/工号最多 64 个字符", ProfileLogic.Validate(new ProfileInput("张三", new string('1', 65)), requireNumber: true));
        Assert.Equal("性别最多 20 个字符", ProfileLogic.Validate(new ProfileInput("张三", "1", new string('男', 21)), requireNumber: true));
        Assert.Equal("手机号最多 30 个字符", ProfileLogic.Validate(new ProfileInput("张三", "1", Phone: new string('1', 31)), requireNumber: true));
        Assert.Equal("邮箱格式不正确", ProfileLogic.Validate(new ProfileInput("张三", "1", Email: "not-an-email"), requireNumber: true));
        Assert.Equal("邮箱格式不正确", ProfileLogic.Validate(new ProfileInput("张三", "1", Email: " 张三 <a@b.com>"), requireNumber: true));
        Assert.Equal("邮箱最多 254 个字符", ProfileLogic.Validate(new ProfileInput("张三", "1", Email: new string('a', 250) + "@b.com"), requireNumber: true));
    }
}
